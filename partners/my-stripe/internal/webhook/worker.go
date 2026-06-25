package webhook

import (
	"bytes"
	"context"
	"log"
	"math"
	"net/http"
	"time"

	"my-stripe/internal/config"
	"my-stripe/internal/store"
)

// on every tick it:
//     - finalize any pending | requires_action intents and refunds whose timer has elaspsed
//     - delivers due webhook events to the payment service with HMAC signing and retries

type Worker struct {
	cfg config.Config
	store *store.Store
	client *http.Client
	logger *log.Logger
}

func NewWorker(cfg config.Config, st *store.Store, logger *log.Logger) *Worker {
	return &Worker {
		cfg: cfg,
		store: st,
		client: &http.Client{
			Timeout: 10 * time.Second,
		},
		logger: logger,
	}
}

func (w *Worker) Run(ctx context.Context) {
	ticker := time.NewTicker(w.cfg.WorkerInterval)
	defer ticker.Stop()

	w.logger.Printf("[worker] started (inverval=%s, finalize_delay=%s, webhook_url=%s)",
		w.cfg.WorkerInterval, w.cfg.FinalizeDelay, w.cfg.WebhookURL)
	
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

	for _, id := range w.store.ExpiresStaleAuthorization(now) {
		w.logger.Printf("[worker] authorization %s expired (hold elapsed); capture will be refused", id)
	}

	for _, f := range w.store.TakeDueFinalizations(now) {
		// only terminal money outcomes produce a customer webhook, a manual-capture
		// authorization that becomes "requres_capture" has updated its stored state
		// but is settled later by an explicit capture, so no webhook is emitted now
		if f.NewStatus != "succeeded" && f.NewStatus != "failed" {
			w.logger.Printf("[worker] finalized %s -> %s (no webhook; awaiting capture)",
			 finalizationRef(f), f.NewStatus)
			 continue
		}
		eventID, eventType, body := BuildEnvelope(f)
		w.store.EnqueueEvent(&store.WebhookEvent{
			ID: eventID,
			Type: eventType,
			Body: body,
			NextAttemptAt: now,
		})

		w.logger.Printf("[worker] finalized %s -> %s, queued %s (%s)",
		finalizationRef(f), f.NewStatus, eventType, eventID)
	}

	for _, ev := range w.store.TakeDueEvents(now, 50) {
		w.deliver(ctx, ev);
	}
}

func (w *Worker) deliver(ctx context.Context, ev *store.WebhookEvent) {
	delivered := w.post(ctx, ev)
	if delivered {
		w.store.MarkEventDelivered(ev.ID)
		w.logger.Printf("[worker] delivered webhook %s (%s)", ev.ID, ev.Type)
		return
	}

	attempt := ev.Attempts + 1
	if attempt >= w.cfg.WebhookMaxAttempts {
		w.store.MarkEventRetry(ev.ID, time.Time{}, true)
		w.logger.Printf("[worker] webhook %s (%s) marked dead after %d attempts; "+
			"payment-service reconciliation will recover it", ev.ID, ev.Type, attempt)
		return
	}

	backoff := time.Duration(math.Pow(2, float64(attempt-1))) * w.cfg.WebhookBaseDelay
	if max := 5 * time.Minute; backoff > max {
		backoff = max
	}
	next := time.Now().UTC().Add(backoff)
	w.store.MarkEventRetry(ev.ID, next, false)
	w.logger.Printf("[worker] webhook %s (%s) attempt %d failed; retrying in %s",
	ev.ID, ev.Type, attempt, backoff)
}

func (w *Worker) post(ctx context.Context, ev *store.WebhookEvent) bool {
	if w.cfg.WebhookURL == "" {
		return false
	}

	reqCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()

	req, err := http.NewRequestWithContext(reqCtx, http.MethodPost, w.cfg.WebhookURL, bytes.NewReader(ev.Body))
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

	if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		return true
	}

	w.logger.Printf("[worker] webhook %s rejected with status %d", ev.ID, resp.StatusCode)
	return false
}

func finalizationRef(f store.Finalization) string {
	if f.Kind == "refund" {
		return "refund " + f.RefundID
	}
	return "payment_intent" + f.PaymentIntentID
}