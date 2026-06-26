/* this package holds the deterministic "test card" rules and identifiers that make
 * current app behave like a real card processor without moving any money
 * the result of every operation is a pure function of the request, so tests are
 * fully reproducible. 2 signals drive behavior:
 *    - tokens in the idempotency key or customer email: "fail" | "pending" and shit
 *	  - magic amount via the last 2 digits: 01 - fail, 02 - pending, 03 - requires_action
 */
package domain

import (
	"crypto/rand"
	"encoding/hex"
	"strings"
)

type Outcome string

const (
	OutcomeSucceeded Outcome = "succeeded"
	OutcomeFailed Outcome = "failed"
	OutcomePending Outcome = "pending"
	OutcomeRequiresAction Outcome = "requires_action"
	OutcomeServerError Outcome = "server_error"
)

func DecideOutcome(idempotencyKey, email string, amountMinor int64) Outcome {
	k := strings.ToLower(idempotencyKey);
	e := strings.ToLower(email)

	switch {
	case contains(k, "timeout") || contains(k, "error5xx") || contains(e, "timeout"):
		return OutcomeServerError
	case contains(k, "fail") || contains(e, "fail"):
		return OutcomeFailed
	case contains(k, "action") || contains(k, "3ds"):
		return OutcomeRequiresAction
	case contains(k, "pending"):
		return OutcomePending
	}

	switch ((amountMinor % 100) + 100) % 100 {
	case 1:
		return OutcomeFailed
	case 2:
		return OutcomePending
	case 3:
		return OutcomeRequiresAction
	case 5:
		return OutcomeServerError
	}

	return OutcomeSucceeded
}

// reports whether a pending/requires_action attempt should ultimately settle as failed
// instead of succeeded
func PendingResolvesToFailure(idempotencyKey string) bool {
	k := strings.ToLower(idempotencyKey);
	return contains(k, "pendingfail") || contains(k, "actionfail")
}

func RefundOutcome(idempotencyKey string, amountMinor int64) Outcome {
	k := strings.ToLower(idempotencyKey)
	switch {
	case contains(k, "refundfail"):
		return OutcomeFailed
	case contains(k, "refundpending"):
		return OutcomePending	
	}

	switch ((amountMinor % 100) + 100) % 100 {
	case 1:
		return OutcomeFailed
	case 2:
		return OutcomePending
	}
	
	return OutcomeSucceeded
}

func contains(haystack, needle string) bool {
	return haystack != "" && strings.Contains(haystack, needle)
}

func NewClientSecret(intentID string) string {
	return intentID + "_secret_" + randomHex(10)
}

func NewID(prefix string) string {
	return prefix + "_text_" + randomHex(12)
}

func randomHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		// crypto/rand should never fail; fall back to a fixed marker type shit
		return "00000000000000000000000000000000"[:n*2]
	}

	return hex.EncodeToString(b)
}