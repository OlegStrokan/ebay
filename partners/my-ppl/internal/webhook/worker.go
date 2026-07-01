package webhook

import (
	"bytes"
	"context"
	"encoding/json"
	"log"
	"math"
	"net/http"
	"time"

	"my-ppl/internal/config"
	"my-ppl/internal/domain"
	"my-ppl/internal/store"
)

// Worker drives PPL's progressive-event simulation. On every tick it:
//   - advances any shipment whose event timer has elapsed one stage (in_transit ->
//     out_for_delivery -> delivered), enqueuing a webhook per registration each stage
//   - delivers due webhook events to the registered callback URL with a plain
//     X-PPL-Webhook-Secret header and exponential-backoff retries
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

	w.logger.Printf("[worker] started (interval=%s, booking_accept=%s, event_spacing=%s, qr_window=%s)",
		w.cfg.WorkerInterval, w.cfg.BookingAcceptDelay, w.cfg.EventSpacing, w.cfg.ReturnQrScanWindow)

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

	transitions, err := w.store.AdvanceDueEvents(now, w.cfg.EventSpacing)
	if err != nil {
		w.logger.Printf("[worker] advancing events failed: %v", err)
	}
	for _, t := range transitions {
		w.enqueueTransitionWebhooks(now, t)
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

func (w *Worker) enqueueTransitionWebhooks(now time.Time, t store.Transition) {
	sh := t.Shipment
	regs, err := w.store.RegistrationsForShipment(sh.PublicID())
	if err != nil {
		w.logger.Printf("[worker] reading registrations for %s failed: %v", sh.PublicID(), err)
		return
	}
	if len(regs) == 0 {
		w.logger.Printf("[worker] %s -> %s but no webhook registered; nothing to fire", sh.PublicID(), t.EventType)
		return
	}

	eventID, body := buildEnvelope(sh, t.EventType)
	for _, reg := range regs {
		// PPL emits the full progressive sequence (in_transit, out_for_delivery,
		// delivered) to every registration regardless of which events it subscribed to.
		// The Gateway validates each one but only acts on the terminal *.delivered event,
		// discarding the intermediates. This is what makes that discard behavior testable.
		if err := w.store.EnqueueEvent(&store.WebhookEvent{
			ID:            eventID + "-" + reg.ID,
			ShipmentID:    sh.PublicID(),
			EventType:     t.EventType,
			CallbackURL:   reg.CallbackURL,
			Body:          body,
			NextAttemptAt: now,
		}); err != nil {
			w.logger.Printf("[worker] enqueue webhook for %s failed: %v", sh.PublicID(), err)
			continue
		}
		w.logger.Printf("[worker] %s -> %s, queued %s", sh.PublicID(), t.EventType, reg.CallbackURL)
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
	ApplyAuth(req, w.cfg.WebhookSecret)

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

// buildEnvelope turns a shipment stage transition into a carrier-tagged event body. The
// "carrier" field lets the Gateway pick plain-secret validation (PPL) over HMAC (DPD).
func buildEnvelope(sh store.Shipment, eventType string) (eventID string, body []byte) {
	eventID = domain.NewID("ppl-evt")

	var data map[string]any
	if sh.Kind == store.KindReturn {
		data = map[string]any{
			"returnShipmentId":     sh.ID,
			"returnTrackingNumber": sh.TrackingNumber,
			"orderId":              sh.OrderID,
			"customerId":           sh.CustomerID,
			"status":               sh.Status,
		}
	} else {
		data = map[string]any{
			"parcelId":       sh.ParcelID,
			"trackingNumber": sh.TrackingNumber,
			"orderId":        sh.OrderID,
			"status":         sh.Status,
		}
	}

	envelope := map[string]any{
		"id":        eventID,
		"object":    "event",
		"type":      eventType,
		"carrier":   "ppl",
		"test_mode": true,
		"data":      data,
	}

	body, _ = json.Marshal(envelope)
	return eventID, body
}
