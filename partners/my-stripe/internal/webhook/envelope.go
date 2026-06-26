package webhook

import (
	"encoding/json"
	"my-stripe/internal/domain"
	"my-stripe/internal/store"
)

// turns a settled intent/refund into a stripe-shaped event body
func BuildEnvelope(f store.Finalization) (eventID string, eventType string, body []byte) {
	eventID = domain.NewID("evt")

	object := map[string]any{
		"metadata": map[string]any{
			"payment_id": f.PaymentID,
		},
		"amount": f.AmountMinor,
		"currency": f.Currency,
	}

	switch f.Kind {
	case "refund":
		object["id"] = f.RefundID
		object["object"] = "refund"
		object["payment_intent"] = f.PaymentIntentID
		if f.NewStatus == "succeeded" {
			eventType = "refund.succeeded"
			object["status"] = "succeeded"
		} else {
			eventType = "refund.failed"
			object["status"] = "failed"
			object["failure_code"] = nonEmpty(f.ErrorCode, "refund_failed")
			object["failure_message"] = nonEmpty(f.ErrorMessage, "Simulated refund failure")
		}
	default: // payment type
		object["id"] = f.PaymentIntentID
		object["object"] = "payment_intent"
		if f.NewStatus == "succeeded" {
			eventType = "payment_intent.succeeded"
			object["status"] = "succeeded"
		} else {
			eventType = "payment_intent.payment_failed"
			object["status"] = "require_payment_method"
			object["last_payment_error"] = map[string]any{
				"code": nonEmpty(f.ErrorCode, "payment_failed"),
				"message": nonEmpty(f.ErrorMessage, "Simulated payment failure."),
			}
		}
	}

	envelope := map[string]any {
		"id": eventID,
		"object": "event",
		"type": eventType,
		"test_mode": true,
		"data": map[string]any{
			"object": object,
		},
	}

	body, _ = json.Marshal(envelope)
	return eventID, eventType, body
}


func nonEmpty(v, fallback string) string {
	if v == "" {
		return fallback
	}
	return v
}