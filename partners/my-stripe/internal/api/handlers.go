package api

import (
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"my-stripe/internal/domain"
	"my-stripe/internal/store"
)


const (
	scopeProcess = "process"
	scopeCapture = "capture"
	scopeRefund = "refund"
)

func (s *Server) handleProcessPayment(w http.ResponseWriter, r *http.Request) {
	var req processPaymentRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	if cached, ok := s.store.Idempotent(scopeProcess, req.IdempotencyKey); ok {
		writeRaw(w, http.StatusOK, cached)
		return
	}

	if req.AmountMinor <= 0 {
		s.respondProcessError(w, "invalid_amount", "Payment amount must be greater then zero")
		return
	}

	if req.PaymentMethod == "" || strings.EqualFold(req.PaymentMethod, "Unknown") {
		s.respondProcessError(w, "invalid_payment_method", "Unkown payment method is not supported")
		return
	}

	outcome := domain.DecideOutcome(req.IdempotencyKey, req.CustomerEmail, req.AmountMinor)
	if outcome == domain.OutcomeServerError {
		// simulate network issue type shit - just to make sure what our partners can code without claude
		writeJSON(w, http.StatusInternalServerError, errorResponse{
			"provider_unavailable", "Simulated provider outage",
		})
		return
	}

	now := time.Now().UTC()
	intentID := domain.NewID("pi")
	pi := &store.PaymentIntent{
		ID: intentID,
		PaymentID: req.PaymentID,
		OrderID: req.OrderID,
		CustomerID: req.CustomerID,
		AmountMinor: req.AmountMinor,
		Currency: strings.ToUpper(req.Currency),
		PaymentMethod: req.PaymentMethod,
		CreatedAt:  now,
		UpdatedAt: now,
	}

	// in manual capture mode a successful authorization stops at "requires_action" insatead
	// of moving money. the funds are only captured by later /{id}capture or released by cancel
	manual := strings.EqualFold(req.CaptureMethod, "manual")
	authorizedStatus := "succeeded"
	if manual {
		authorizedStatus = "requires_capture"
		// the hold is valid until it's expires
		pi.ExpiresAt = now.Add(s.cfg.AuthHoldTTL)
	}

	var resp paymentIntentResponse
	switch outcome {
	case domain.OutcomeSucceeded:
		pi.Status = authorizedStatus
		resp = paymentIntentResponse{
			ID: intentID,
			Status: authorizedStatus, 
			TestMode: true,
		}

	case domain.OutcomeFailed:
		pi.Status = "failed"
		pi.ErrorCode = "card_declined"
		pi.ErrorMessage = "Simulated card decline"
		resp = paymentIntentResponse{
			ID: intentID,
			Status: "failed",
			ErrorCode: pi.ErrorCode,
			ErrorMessage: pi.ErrorMessage,
			TestMode: true,
		}
	
	case domain.OutcomePending:
		pi.Status = "pending"
		s.scheduleFinalize(&pi.FinalizeAt, &pi.FinalizedTo, &pi.FinalizeCode, &pi.FinalizeMsg,
		req.IdempotencyKey, authorizedStatus, now)
		resp = paymentIntentResponse{
			ID: intentID,
			Status: "pending",
			TestMode: true,
		}

	case domain.OutcomeRequiresAction:
		pi.Status = "requires_action"
		pi.ClientSecret = domain.NewClientSecret(intentID)
		s.scheduleFinalize(&pi.FinalizeAt, &pi.FinalizedTo, &pi.FinalizeCode, &pi.FinalizeMsg,
		req.IdempotencyKey, authorizedStatus, now)
		resp = paymentIntentResponse{
			ID: intentID,
			Status: "requires_action",
			ClientSecret: pi.ClientSecret,
			TestMode: true,
		}
	}

	s.store.PutIntent(pi)
	s.writeAndCache(w, scopeProcess, req.IdempotencyKey, resp)
}

func (s *Server) handleCapture(w http.ResponseWriter, r *http.Request) {
	intentID := r.PathValue("id")

	var req capturePaymentRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	if cached, ok := s.store.Idempotent(scopeCapture, req.IdempotencyKey); ok {
		writeRaw(w, http.StatusOK, cached)
		return
	}

	if existing, ok := s.store.GetIntent(intentID); ok && existing.Status == "canceled" {
		resp := captureResponse{
			ID: intentID,
			Status: "failed",
			ErrorCode: "intent_canceled",
			ErrorMessage: "Cannot capture a canceled authorization",
			TestMode: true,
		}
		s.writeAndCache(w, scopeCapture, req.IdempotencyKey, resp)
		return
	}

	if existing, ok := s.store.GetIntent(intentID); ok && existing.Status == "expired" {
		resp := captureResponse{
			ID: intentID,
			Status: "failed",
			ErrorCode: "authorization_expired",
			ErrorMessage: "Authorization hold expired before capture",
			TestMode: true,
		}
		s.writeAndCache(w, scopeCapture, req.IdempotencyKey, resp)
		return
	}

	// fail is client simulated failure case, success othervise + the intent may
	// not exists in the store, so we tolerate a missing record like real stripe
	failed := strings.Contains(strings.ToLower(intentID), "fail")

	var resp captureResponse
	if failed {
		resp = captureResponse{
			ID: intentID,
			Status: "failed",
			ErrorCode: "capture_failed",
			ErrorMessage: "Simulated capture failure.",
			TestMode: true,
		}
		s.store.UpdateIntentStatus(intentID, "failed", resp.ErrorCode, resp.ErrorMessage)
	} else {
		resp = captureResponse{
			ID: intentID,
			Status: "succeeded",
			TestMode: true,
		}
		s.store.UpdateIntentStatus(intentID, "succeeded", "", "")
	}
	s.writeAndCache(w, scopeCapture, req.IdempotencyKey, resp)
}

func (s *Server) handleCancel(w http.ResponseWriter, r *http.Request) {
	intentID := r.PathValue("id")
	s.store.UpdateIntentStatus(intentID, "canceled", "", "")
	writeJSON(w, http.StatusOK, cancelResponse{
		ID: intentID,
		Status: "canceled",
		TestMode: true,
	})
}

func (s *Server) handleRefund(w http.ResponseWriter, r *http.Request) {
	var req refundRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	if cached, ok := s.store.Idempotent(scopeRefund, req.IdempotencyKey); ok {
		writeRaw(w, http.StatusOK, cached)
		return
	}

	if req.AmountMinor <= 0 {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_amount", "Refund amount must be greater than zero"})
		return
	}

	if req.PaymentIntentID == "" {
		writeJSON(w, http.StatusBadRequest, errorResponse{
			"missing_payment_intent_id", "payment_intent_id is required for refunds",
		})
		return
	}

	now :=time.Now().UTC()
	refundID := domain.NewID("re")
	refund := &store.Refund{
		ID: refundID,
		PaymentIntentID: req.PaymentIntentID,
		PaymentID: req.PaymentID,
		AmountMinor: req.AmountMinor,
		Currency: strings.ToUpper(req.Currency),
		Reason: req.Reason,
		CreatedAt: now,
		UpdatedAt: now,
	}

	var resp refundResponse
	switch domain.RefundOutcome(req.IdempotencyKey, req.AmountMinor) {
	case domain.OutcomeFailed:
		refund.Status = "failed"
		refund.ErrorCode = "refund_failed"
		refund.ErrorMessage = "Simulated refund failure"
		resp = refundResponse{
			ID: refundID,
			Status: "failed",
			ErrorCode: refund.ErrorCode,
			ErrorMessage: refund.ErrorMessage,
			TestMode: true,
		}

	case domain.OutcomePending:
		refund.Status = "pending"
		refund.FinalizeAt = now.Add(s.cfg.FinalizeDelay)
		refund.FinalizeTo = "succeeded"
		resp = refundResponse{
			ID: refundID,
			Status: "pending", 
			TestMode: true,
		}

	default:
		refund.Status = "succeeded"
		resp = refundResponse{
			ID: refundID,
			Status: "succeeded",
			TestMode: true,
		}
	}

	s.store.PutRefund(refund)
	s.writeAndCache(w, scopeRefund, req.IdempotencyKey, resp)
}

func (s *Server) handleGetPaymentStatus(w http.ResponseWriter, r *http.Request) {
	intentID := r.PathValue("id")
	pi, ok := s.store.GetIntent(intentID)

	if !ok {
		writeJSON(w, http.StatusNotFound, statusResponse{
			ID: intentID,
			Status: "unknown",
			TestMode: true,
		})
		return
	}
	writeJSON(w, http.StatusOK, statusResponse{
		ID: pi.ID,
		Status: lifecycle(pi.Status),
		ErrorCode: pi.ErrorCode,
		ErrorMessage: pi.ErrorMessage,
		TestMode: true,
	})
}

func (s *Server) handleGetRefundStatus(w http.ResponseWriter, r *http.Request) {
	refundID := r.PathValue("id")
	refund, ok := s.store.GetRefund(refundID)

	if !ok {
		writeJSON(w, http.StatusNotFound, statusResponse{
			ID: refundID,
			Status: "unknown",
			TestMode: true,
		})
		return
	}
	writeJSON(w, http.StatusOK, statusResponse{
		ID: refund.ID,
		Status: lifecycle(refund.Status),
		ErrorCode: refund.ErrorCode,
		ErrorMessage: refund.ErrorMessage,
		TestMode: true,
	})
}


// helpers

func (s *Server) scheduleFinalize(at *time.Time, to, code, msg *string, idempotencyKey, successTarget string, now time.Time) {
	*at = now.Add(s.cfg.FinalizeDelay)
	if domain.PendingResolvesToFailure(idempotencyKey) {
		*to = "failed"
		*code = "card_declined"
		*msg = "Simulate declined after authentication"
		return
	}
	*to = successTarget
}

func (s *Server) respondProcessError(w http.ResponseWriter, code, message string) {
	writeJSON(w, http.StatusBadRequest, paymentIntentResponse{
		Status: "failed", ErrorCode: code, ErrorMessage: message, TestMode: true,
	})
}

func (s *Server) writeAndCache(w http.ResponseWriter, scope, key string, body any) {
	encoded, err := json.Marshal(body)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to encode resposne"})
		return
	}
	s.store.SaveIdempotent(scope, key, encoded)
	writeRaw(w, http.StatusOK, encoded)
}

func writeRaw(w http.ResponseWriter, status int, body []byte) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = w.Write(body)
}

func lifecycle(status string) string {
	switch status {
	case "succeeded":
		return "succeeded"
	case "failed", "cancelled", "expired":
		return "failed"
	case "pending", "requires_action", "requires_capture":
		return "pending"
	default:
		return "unknown"
	}
}