package api

import (
	"bytes"
	"encoding/json"
	"log"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"my-stripe/internal/config"
	"my-stripe/internal/store"
)

func newTestServer(t *testing.T) (*httptest.Server, *store.Store, config.Config) {
	t.Helper()
	cfg := config.Config{APIKey: "test_key"}
	st := store.New()
	logger := log.New(bytes.NewBuffer(nil), "", 0)
	srv := NewServer(cfg, st, logger)
	ts := httptest.NewServer(srv.Handler())
	t.Cleanup(ts.Close)
	return ts, st, cfg
}

func doCancel(t *testing.T, ts *httptest.Server, cfg config.Config, intentID string) *http.Response {
	t.Helper()
	req, err := http.NewRequest(http.MethodPost, ts.URL+"/v1/payment-intents/"+intentID+"/cancel", nil)
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Authorization", "Bearer "+cfg.APIKey)

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = resp.Body.Close() })
	return resp
}

func doCapture(t *testing.T, ts *httptest.Server, cfg config.Config, intentID, idempotencyKey string) *http.Response {
	t.Helper()
	payload, err := json.Marshal(capturePaymentRequest{IdempotencyKey: idempotencyKey})
	if err != nil {
		t.Fatal(err)
	}
	req, err := http.NewRequest(http.MethodPost, ts.URL+"/v1/payment-intents/"+intentID+"/capture", bytes.NewReader(payload))
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Authorization", "Bearer "+cfg.APIKey)
	req.Header.Set("Content-Type", "application/json")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = resp.Body.Close() })
	return resp
}

func decodeErrorResponse(t *testing.T, resp *http.Response) errorResponse {
	t.Helper()
	var body errorResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	return body
}

func TestHandleCancel_UnknownIntent_Returns404(t *testing.T) {
	ts, _, cfg := newTestServer(t)

	resp := doCancel(t, ts, cfg, "pi_does_not_exist")

	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("got status %d, want %d", resp.StatusCode, http.StatusNotFound)
	}
	body := decodeErrorResponse(t, resp)
	if body.ErrorCode != "resource_missing" {
		t.Fatalf("got error_code %q, want resource_missing", body.ErrorCode)
	}
}

func TestHandleCancel_AlreadySucceeded_Returns400(t *testing.T) {
	ts, st, cfg := newTestServer(t)
	st.PutIntent(&store.PaymentIntent{ID: "pi_1", Status: "succeeded", CreatedAt: time.Now().UTC()})

	resp := doCancel(t, ts, cfg, "pi_1")

	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("got status %d, want %d", resp.StatusCode, http.StatusBadRequest)
	}
	body := decodeErrorResponse(t, resp)
	if body.ErrorCode != "payment_intent_unexpected_state" {
		t.Fatalf("got error_code %q, want payment_intent_unexpected_state", body.ErrorCode)
	}

	// the intent must not have been mutated by the rejected cancel
	pi, ok := st.GetIntent("pi_1")
	if !ok || pi.Status != "succeeded" {
		t.Fatalf("expected pi_1 to remain succeeded, got %+v (found=%v)", pi, ok)
	}
}

func TestHandleCancel_CancelableIntent_Returns200(t *testing.T) {
	ts, st, cfg := newTestServer(t)
	st.PutIntent(&store.PaymentIntent{ID: "pi_2", Status: "requires_capture", CreatedAt: time.Now().UTC()})

	resp := doCancel(t, ts, cfg, "pi_2")

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("got status %d, want %d", resp.StatusCode, http.StatusOK)
	}
	var body cancelResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	if body.Status != "canceled" {
		t.Fatalf("got status %q, want canceled", body.Status)
	}

	pi, ok := st.GetIntent("pi_2")
	if !ok || pi.Status != "canceled" {
		t.Fatalf("expected pi_2 to become canceled, got %+v (found=%v)", pi, ok)
	}
}

func TestHandleCapture_UnknownIntent_Returns404(t *testing.T) {
	ts, _, cfg := newTestServer(t)

	resp := doCapture(t, ts, cfg, "pi_does_not_exist", "idem-1")

	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("got status %d, want %d", resp.StatusCode, http.StatusNotFound)
	}
	body := decodeErrorResponse(t, resp)
	if body.ErrorCode != "resource_missing" {
		t.Fatalf("got error_code %q, want resource_missing", body.ErrorCode)
	}
}

func TestHandleCapture_KnownIntent_Returns200AndQueuesWebhook(t *testing.T) {
	ts, st, cfg := newTestServer(t)
	st.PutIntent(&store.PaymentIntent{ID: "pi_3", Status: "requires_capture", CreatedAt: time.Now().UTC()})

	resp := doCapture(t, ts, cfg, "pi_3", "idem-2")

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("got status %d, want %d", resp.StatusCode, http.StatusOK)
	}
	var body captureResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	if body.Status != "succeeded" {
		t.Fatalf("got status %q, want succeeded", body.Status)
	}

	due := st.TakeDueEvents(time.Now().UTC(), 10)
	if len(due) != 1 {
		t.Fatalf("expected exactly 1 queued webhook event for a known intent, got %d", len(due))
	}
}

