/* this package holds the deterministic "magic token" rules and identifiers that make
 * the fake DPD carrier behave like a real depot network without moving any parcels.
 * the result of every operation is a pure function of the request, so saga tests are
 * fully reproducible. two signals drive behavior:
 *   - magic tokens inside the orderId: "fail" | "lost" | "slow" | "error5xx" | "returnfail"
 *   - the recipient postal code suffix: ...01 -> invalid address, ...05 -> server error
 */
package domain

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"strings"
)

type Outcome string

const (
	OutcomeSuccess        Outcome = "success"
	OutcomeInvalidAddress Outcome = "invalid_address"
	OutcomeServerError    Outcome = "server_error"
)

// DecideOutcome resolves the result of an outbound shipment create.
//
//	orderId contains "error5xx"  -> 500 (DPD API outage, saga retries the step)
//	orderId contains "fail"      -> 400 invalid address (saga compensation)
//	postal code ends in "05"     -> 500 server error
//	postal code ends in "01"     -> 400 invalid address
//	anything else                -> 201 success
//
// "returnfail" is explicitly excluded from the outbound "fail" rule so a saga can
// ship the outbound parcel successfully and only fail later on the return leg.
func DecideOutcome(orderID, postalCode string) Outcome {
	o := strings.ToLower(orderID)
	switch {
	case contains(o, "error5xx"):
		return OutcomeServerError
	case contains(o, "fail") && !contains(o, "returnfail"):
		return OutcomeInvalidAddress
	}

	switch {
	case strings.HasSuffix(postalCode, "05"):
		return OutcomeServerError
	case strings.HasSuffix(postalCode, "01"):
		return OutcomeInvalidAddress
	}

	return OutcomeSuccess
}

// DecideReturnOutcome resolves the result of a return shipment create.
//
//	orderId contains "returnfail" -> 400 invalid address
//	anything else                 -> 201 success
func DecideReturnOutcome(orderID string) Outcome {
	if contains(strings.ToLower(orderID), "returnfail") {
		return OutcomeInvalidAddress
	}
	return OutcomeSuccess
}

// IsLost reports whether the shipment must be created but never delivered, so the
// saga's await step eventually times out.
func IsLost(orderID string) bool {
	return contains(strings.ToLower(orderID), "lost")
}

// IsSlow reports whether the outbound finalize delay must be stretched ×10 to
// simulate a backed-up depot.
func IsSlow(orderID string) bool {
	return contains(strings.ToLower(orderID), "slow")
}

func contains(haystack, needle string) bool {
	return haystack != "" && strings.Contains(haystack, needle)
}

// NewID returns an identifier shaped like "<prefix>-<uuid>", e.g. "dpd-shp-<uuid>".
func NewID(prefix string) string {
	return prefix + "-" + newUUID()
}

func newUUID() string {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		// crypto/rand should never fail; fall back to the nil UUID.
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
