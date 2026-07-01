/* this package holds the deterministic "magic token" rules and identifiers that make
 * the fake PPL carrier behave like a real partner-depot network without moving any
 * parcels. PPL differs from DPD: booking is two-phase (submit then poll), cancellation
 * is refused once the parcel is in the network, and webhooks use a plain shared secret.
 * two signals drive behavior:
 *   - magic tokens inside the orderId: "fail" | "error5xx" | "pollreject" |
 *     "cancelblock" | "slowpoll" | "returnfail"
 *   - the recipient postal code suffix: ...01 -> invalid, ...05 -> 500, ...09 -> rejected
 */
package domain

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"strings"
)

// CreateOutcome is the synchronous result of POST /api/v1/parcels.
type CreateOutcome string

const (
	CreateSuccess        CreateOutcome = "success"
	CreateInvalidAddress CreateOutcome = "invalid_address"
	CreateServerError    CreateOutcome = "server_error"
)

// PollOutcome is the deferred result the booking settles to when the accept timer
// elapses: the depot either accepts the parcel or rejects it for capacity.
type PollOutcome string

const (
	PollAccepted PollOutcome = "success"
	PollRejected PollOutcome = "poll_fail"
)

// DecideCreateOutcome resolves the immediate HTTP result of submitting a booking.
//
//	orderId contains "error5xx" -> 500
//	orderId contains "fail"     -> 422 invalid address (excludes "returnfail")
//	postal code ends in "05"    -> 500 server error
//	postal code ends in "01"    -> 422 invalid address
//	anything else               -> 202 accepted-for-processing
func DecideCreateOutcome(orderID, postalCode string) CreateOutcome {
	o := strings.ToLower(orderID)
	switch {
	case contains(o, "error5xx"):
		return CreateServerError
	case contains(o, "fail") && !contains(o, "returnfail"):
		return CreateInvalidAddress
	}

	switch {
	case strings.HasSuffix(postalCode, "05"):
		return CreateServerError
	case strings.HasSuffix(postalCode, "01"):
		return CreateInvalidAddress
	}

	return CreateSuccess
}

// DecidePollOutcome resolves what an accepted-for-processing booking settles to when
// polled after the accept delay.
//
//	orderId contains "pollreject" -> rejected
//	postal code ends in "09"      -> rejected
//	anything else                 -> accepted
func DecidePollOutcome(orderID, postalCode string) PollOutcome {
	if contains(strings.ToLower(orderID), "pollreject") || strings.HasSuffix(postalCode, "09") {
		return PollRejected
	}
	return PollAccepted
}

// DecideReturnOutcome resolves the result of a return create.
//
//	orderId contains "returnfail" -> 422 invalid address
//	anything else                 -> 201 success
func DecideReturnOutcome(orderID string) CreateOutcome {
	if contains(strings.ToLower(orderID), "returnfail") {
		return CreateInvalidAddress
	}
	return CreateSuccess
}

// IsCancelBlock reports whether the parcel must enter the network immediately on accept,
// so a later cancel is refused with 409.
func IsCancelBlock(orderID string) bool {
	return contains(strings.ToLower(orderID), "cancelblock")
}

// IsSlowPoll reports whether the booking-accept delay must be stretched ×5 so the
// adapter's polling loop times out.
func IsSlowPoll(orderID string) bool {
	return contains(strings.ToLower(orderID), "slowpoll")
}

func contains(haystack, needle string) bool {
	return haystack != "" && strings.Contains(haystack, needle)
}

// NewID returns an identifier shaped like "<prefix>-<uuid>", e.g. "ppl-ref-<uuid>".
func NewID(prefix string) string {
	return prefix + "-" + newUUID()
}

func newUUID() string {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "00000000-0000-0000-0000-000000000000"
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%s-%s-%s-%s-%s",
		hex.EncodeToString(b[0:4]),
		hex.EncodeToString(b[4:6]),
		hex.EncodeToString(b[6:8]),
		hex.EncodeToString(b[8:10]),
		hex.EncodeToString(b[10:16]),
	)
}
