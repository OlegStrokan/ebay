package api

type processPaymentRequest struct {
	PaymentID string `json:"payment_id"`
	OrderID string `json:"order_id"`
	CustomerID string `json:"customer_id"`
	AmountMinor int64 `json:"amount_minor"`
	Currency string `json:"currency"`
	PaymentMethod string `json:"payment_method"`
	IdempotencyKey string `json:"idempotency_key"`
	CustomerEmail string `json:"customer_email"`
	// capture method is "automatic" (authorize + capture) or manual (authorize only)
	CaptureMethod string `json:"capture_method"`
}

type capturePaymentRequest struct {
	PaymentID string `json:"payment_id"`
	OrderID string `json:"order_id"`
	CustomerID string `json:"customer_id"`
	AmountMinor int64 `json:"amount_minor"`
	Currency  string `json:"currency"`
	IdempotencyKey string `json:"idempotency_key"`
}

type refundRequest struct {
	PaymentID string `json:"payment_id"`
	PaymentIntentID string `json:"payment_intent_id"`
	AmountMinor int64 `json:"amount_minor"`
	Currency string `json:"currency"`
	Reason string `json:"reason"`
	IdempotencyKey string `json:"idempotency_key"`
}

type paymentIntentResponse struct {
	ID string `json:"id"`
	Status string `json:"status"` // succeeded | failed | pending | requires_action
	ClientSector string `json:"client_status,omitempty"`
	ErrorCode string `json:"error_code,omitempty"`
	ErrorMessage string `json:"error_message,omitempty"`
	TestMode bool `json:"test_mode"`
}

type captureResponse struct {
	ID string `json:"id"`
	Status string `json:"status"`
	ErrorCode string `json:"error_code,omitempty"`
	ErrorMessage string `json:"error_message,omitempty"`
	TestMode bool `json:"test_mode"`
}

type cancelResponse struct {
	ID string `json:"id"`
	Status string `json:"status"` // canceled
	TestMode bool `json:"test_mode"`
}

type refundResponse struct {
	ID string `json:"id"`
	Status string `json:"status"` // succeeded | pending | failed
	ErrorCode string `json:"error_code,omitempty"`
	ErrorMessage string `json:"error_message,omitempty"`
	TestMode bool `json:"test_mode"`
}

type statusResponse struct {
	ID string `json:"id"`
	Status string `json:"status"` // succeeded | pending | failed | unknown
	ErrorCode string `json:"error_code,omitempty"`
	ErrorMessage string `json:"error_message,omitempty"`
	TestMode bool `json:"test_mode"`
}

type errorResponse struct {
	ErrorCode string `json:"error_code"`
	ErrorMessage string `json:"error_message"`
}



