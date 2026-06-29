package webhook

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
)

// Sign produces a Stripe-compatible signature header value: "t={ts},v1={hmac}".
// DPD reuses the exact scheme my-stripe uses; the Gateway validates this header for
// DPD shipping webhooks.
func Sign(secret string, body []byte, timestamp int64) string {
	signedPayload := fmt.Sprintf("%d.%s", timestamp, body)
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(signedPayload))
	signature := hex.EncodeToString(mac.Sum(nil))
	return fmt.Sprintf("t=%d,v1=%s", timestamp, signature)
}
