package webhook

import "net/http"

// ApplyAuth attaches PPL's plain shared-secret header to an outbound webhook request.
// Unlike DPD/Stripe, PPL does NOT HMAC-sign the body; the Gateway validates this header
// value against WebhookSecurity:PplSharedSecret.
func ApplyAuth(req *http.Request, secret string) {
	req.Header.Set("X-PPL-Webhook-Secret", secret)
}
