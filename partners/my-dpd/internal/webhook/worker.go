package webhook

import (
	"bytes"
	"context"
	"encoding/json"
	"log"
	"math"
	"net/http"
	"time"

	"my-dpd/internal/config"
	"my-dpd/internal/domain"
	"my-dpd/internal/store"
)

// Worker drives the physical-delivery simulation. On every tick it:
//   - advances any shipment whose timer has elapsed (created -> in_transit -> delivered
//     for outbound; awaiting_pickup -> delivered for returns)
//   - enqueues a webhook per registration when a shipment reaches "delivered"
//   - delivers due webhook events to the registered callback URL, HMAC-signed, with
//     exponential-backoff retries until success or a dead-letter cap
type Worker struct {
	cfg    config.Config
	store  *store.Store
	client *http.Client
	logger *log.Logger
}

func NewWorker(cfg config.Config, st *store.Store, logger *log.Logger) *Worker {
	return &Worker{
		cfg:    cfg,
		store:  st,
		client: &http.Client{Timeout: 10 * time.Second},
		logger: logger,
	}
}

func (w *Worker) Run(ctx context.Context) {
	ticker := time.NewTicker(w.cfg.WorkerInterval)
	defer ticker.Stop()

	w.logger.Printf("[worker] started (interval=%s, shipment_finalize=%s, return_finalize=%s)",
		w.cfg.WorkerInterval, w.cfg.ShipmentFinalizeDelay, w.cfg.ReturnFinalizeDelay)

	for {
		select {
		case <-ctx.Done():
			w.logger.Println("[worker] stopped")
			return
		case <-ticker.C:
			w.tick(ctx)
		}
	}
}

func (w *Worker) tick(ctx context.Context) {
	now := time.Now().UTC()

	delivered, err := w.store.AdvanceDueShipments(now, w.cfg.StatusAdvanceGap)
	if err != nil {
		w.logger.Printf("[worker] advancing shipments failed: %v", err)
	}
	for _, sh := range delivered {
		w.enqueueDeliveredWebhooks(now, sh)
	}

	due, err := w.store.TakeDueEvents(now, 50)
	if err != nil {
		w.logger.Printf("[worker] reading due events failed: %v", err)
		return
	}
	for _, ev := range due {
		w.deliver(ctx, ev)
	}
}

func (w *Worker) enqueueDeliveredWebhooks(now time.Time, sh store.Shipment) {
	eventType := "shipment.delivered"
	if sh.Kind == store.KindReturn {
		eventType = "return.delivered"
	}

	regs, err := w.store.RegistrationsForShipment(sh.ID)
	if err != nil {
		w.logger.Printf("[worker] reading registrations for %s failed: %v", sh.ID, err)
		return
	}
	if len(regs) == 0 {
		w.logger.Printf("[worker] %s delivered but no webhook registered; nothing to fire", sh.ID)
		return
	}

	eventID, body := buildEnvelope(sh, eventType)
	for _, reg := range regs {
		if !eventWanted(reg.Events, eventType) {
			continue
		}
		if err := w.store.EnqueueEvent(&store.WebhookEvent{
			ID:            eventID + "-" + reg.ID,
			ShipmentID:    sh.ID,
			EventType:     eventType,
			CallbackURL:   reg.CallbackURL,
			Body:          body,
			NextAttemptAt: now,
		}); err != nil {
			w.logger.Printf("[worker] enqueue webhook for %s failed: %v", sh.ID, err)
			continue
		}
		w.logger.Printf("[worker] %s delivered, queued %s -> %s", sh.ID, eventType, reg.CallbackURL)
	}
}

func (w *Worker) deliver(ctx context.Context, ev store.WebhookEvent) {
	if w.post(ctx, ev) {
		if err := w.store.MarkEventDelivered(ev.ID); err != nil {
			w.logger.Printf("[worker] mark delivered failed for %s: %v", ev.ID, err)
		}
		w.logger.Printf("[worker] delivered webhook %s (%s)", ev.ID, ev.EventType)
		return
	}

	attempt := ev.Attempts + 1
	if attempt >= w.cfg.WebhookMaxAttempts {
		if err := w.store.MarkEventRetry(ev.ID, time.Time{}, true); err != nil {
			w.logger.Printf("[worker] mark dead failed for %s: %v", ev.ID, err)
		}
		w.logger.Printf("[worker] webhook %s (%s) marked dead after %d attempts", ev.ID, ev.EventType, attempt)
		return
	}

	backoff := time.Duration(math.Pow(2, float64(attempt-1))) * w.cfg.WebhookBaseDelay
	if maxBackoff := 5 * time.Minute; backoff > maxBackoff {
		backoff = maxBackoff
	}
	next := time.Now().UTC().Add(backoff)
	if err := w.store.MarkEventRetry(ev.ID, next, false); err != nil {
		w.logger.Printf("[worker] schedule retry failed for %s: %v", ev.ID, err)
	}
	w.logger.Printf("[worker] webhook %s (%s) attempt %d failed; retrying in %s", ev.ID, ev.EventType, attempt, backoff)
}

func (w *Worker) post(ctx context.Context, ev store.WebhookEvent) bool {
	if ev.CallbackURL == "" {
		return false
	}

	reqCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()

	req, err := http.NewRequestWithContext(reqCtx, http.MethodPost, ev.CallbackURL, bytes.NewReader(ev.Body))
	if err != nil {
		w.logger.Printf("[worker] failed to build webhook request: %v", err)
		return false
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Stripe-Signature", Sign(w.cfg.WebhookSecret, ev.Body, time.Now().UTC().Unix()))

	resp, err := w.client.Do(req)
	if err != nil {
		w.logger.Printf("[worker] webhook POST error for %s: %v", ev.ID, err)
		return false
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		return true
	}
	w.logger.Printf("[worker] webhook %s rejected with status %d", ev.ID, resp.StatusCode)
	return false
}

// buildEnvelope turns a delivered shipment into a carrier-tagged event body. The
// "carrier" field lets the Gateway pick HMAC validation (DPD) over plain-secret (PPL).
func buildEnvelope(sh store.Shipment, eventType string) (eventID string, body []byte) {
	eventID = domain.NewID("dpd-evt")

	var data map[string]any
	if sh.Kind == store.KindReturn {
		data = map[string]any{
			"returnShipmentId":     sh.ID,
			"returnTrackingNumber": sh.TrackingNumber,
			"orderId":              sh.OrderID,
			"customerId":           sh.CustomerID,
			"status":               store.StatusDelivered,
		}
	} else {
		data = map[string]any{
			"shipmentId":     sh.ID,
			"trackingNumber": sh.TrackingNumber,
			"orderId":        sh.OrderID,
			"status":         store.StatusDelivered,
		}
	}

	envelope := map[string]any{
		"id":        eventID,
		"object":    "event",
		"type":      eventType,
		"carrier":   "dpd",
		"test_mode": true,
		"data":      data,
	}

	body, _ = json.Marshal(envelope)
	return eventID, body
}

// eventWanted reports whether a registration subscribed to the given event type. An
// empty subscription list (or "*") means "all events".
func eventWanted(subscribed []string, eventType string) bool {
	if len(subscribed) == 0 {
		return true
	}
	for _, e := range subscribed {
		if e == eventType || e == "*" {
			return true
		}
	}
	return false
}
