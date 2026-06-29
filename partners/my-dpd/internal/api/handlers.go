package api

import (
	"net/http"
	"strings"
	"time"

	"my-dpd/internal/domain"
	"my-dpd/internal/store"
)

// handleCreateShipment creates an outbound shipment. DPD answers synchronously and
// authoritatively: the result is a pure function of the orderId and postal code.
func (s *Server) handleCreateShipment(w http.ResponseWriter, r *http.Request) {
	var req createShipmentRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	oid := effectiveOrderID(req.OrderID, r)

	switch domain.DecideOutcome(oid, req.Recipient.PostalCode) {
	case domain.OutcomeServerError:
		// Simulated DPD API outage; the saga retries the CreateShipmentStep.
		writeJSON(w, http.StatusInternalServerError, errorResponse{
			"carrier_unavailable", "Simulated DPD depot outage",
		})
		return
	case domain.OutcomeInvalidAddress:
		// Triggers InvalidAddressException in the adapter -> saga compensation.
		writeJSON(w, http.StatusBadRequest, errorResponse{
			"invalid_address", "Recipient address could not be validated by DPD",
		})
		return
	}

	now := time.Now().UTC()
	shipment := &store.Shipment{
		ID:             domain.NewID("dpd-shp"),
		Kind:           store.KindOutbound,
		OrderID:        req.OrderID,
		TrackingNumber: domain.NewID("dpd-trk"),
		PostalCode:     req.Recipient.PostalCode,
		Status:         store.StatusCreated,
		FinalizeAt:     outboundFinalizeAt(now, oid, s.cfg.ShipmentFinalizeDelay),
		CreatedAt:      now,
		UpdatedAt:      now,
	}

	if err := s.store.PutShipment(shipment); err != nil {
		s.logger.Printf("[api] failed to persist shipment: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to persist shipment"})
		return
	}

	writeJSON(w, http.StatusCreated, createShipmentResponse{
		ShipmentID:     shipment.ID,
		TrackingNumber: shipment.TrackingNumber,
		Status:         shipment.Status,
		TestMode:       true,
	})
}

// handleCancelShipment is always idempotent and never errors: DPD lets you cancel
// until a driver scans the label, and the adapter treats any 2xx (and 404) as success.
func (s *Server) handleCancelShipment(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")

	shipment, ok, err := s.store.GetShipment(id)
	if err != nil {
		s.logger.Printf("[api] cancel lookup failed: %v", err)
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if !ok {
		// Unknown shipment: idempotent success.
		w.WriteHeader(http.StatusNoContent)
		return
	}

	// Once the parcel is moving DPD can no longer recall it, but cancel still reports
	// success (idempotent) so compensation does not fail on an already-shipped parcel.
	if shipment.Status == store.StatusInTransit || shipment.Status == store.StatusDelivered {
		w.WriteHeader(http.StatusNoContent)
		return
	}

	if _, _, err := s.store.UpdateShipmentStatus(id, store.StatusCancelled, time.Time{}); err != nil {
		s.logger.Printf("[api] cancel update failed: %v", err)
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleGetStatus(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	shipment, ok, err := s.store.GetShipment(id)
	if err != nil {
		s.logger.Printf("[api] status lookup failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to read shipment"})
		return
	}
	if !ok {
		writeJSON(w, http.StatusNotFound, statusResponse{ShipmentID: id, Status: "unknown", TestMode: true})
		return
	}
	writeJSON(w, http.StatusOK, statusResponse{
		ShipmentID:     shipment.ID,
		TrackingNumber: shipment.TrackingNumber,
		Status:         shipment.Status,
		TestMode:       true,
	})
}

func (s *Server) handleRegisterWebhook(w http.ResponseWriter, r *http.Request) {
	var req registerWebhookRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}
	if req.ShipmentID == "" || req.CallbackURL == "" {
		writeJSON(w, http.StatusBadRequest, errorResponse{
			"invalid_request", "shipmentId and callbackUrl are required",
		})
		return
	}

	reg := &store.WebhookRegistration{
		ID:          domain.NewID("dpd-whk"),
		ShipmentID:  req.ShipmentID,
		CallbackURL: req.CallbackURL,
		Events:      req.Events,
		CreatedAt:   time.Now().UTC(),
	}
	if err := s.store.PutRegistration(reg); err != nil {
		s.logger.Printf("[api] failed to persist webhook registration: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to register webhook"})
		return
	}

	writeJSON(w, http.StatusOK, registerWebhookResponse{Registered: true})
}

func (s *Server) handleCreateReturn(w http.ResponseWriter, r *http.Request) {
	var req createReturnRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	oid := effectiveOrderID(req.OrderID, r)

	if domain.DecideReturnOutcome(oid) == domain.OutcomeInvalidAddress {
		writeJSON(w, http.StatusBadRequest, errorResponse{
			"invalid_address", "Return pickup address could not be validated by DPD",
		})
		return
	}

	now := time.Now().UTC()
	expectedPickup := now.Add(48 * time.Hour)
	shipment := &store.Shipment{
		ID:                 domain.NewID("dpd-ret"),
		Kind:               store.KindReturn,
		OrderID:            req.OrderID,
		CustomerID:         req.CustomerID,
		TrackingNumber:     domain.NewID("dpd-rtrk"),
		Status:             store.StatusAwaitingPickup,
		FinalizeAt:         now.Add(s.cfg.ReturnFinalizeDelay),
		ExpectedPickupDate: expectedPickup,
		CreatedAt:          now,
		UpdatedAt:          now,
	}

	if err := s.store.PutShipment(shipment); err != nil {
		s.logger.Printf("[api] failed to persist return shipment: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to persist return shipment"})
		return
	}

	writeJSON(w, http.StatusCreated, createReturnResponse{
		ReturnShipmentID:     shipment.ID,
		ReturnTrackingNumber: shipment.TrackingNumber,
		ExpectedPickupDate:   expectedPickup.Format(time.RFC3339),
		TestMode:             true,
	})
}

func (s *Server) handleCancelReturn(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if shipment, ok, err := s.store.GetShipment(id); err == nil && ok &&
		shipment.Status != store.StatusDelivered {
		if _, _, err := s.store.UpdateShipmentStatus(id, store.StatusCancelled, time.Time{}); err != nil {
			s.logger.Printf("[api] cancel return update failed: %v", err)
		}
	}
	w.WriteHeader(http.StatusNoContent)
}

// effectiveOrderID appends the X-Carrier-Test-Scenario header value to the orderId so
// that E2E tests can inject magic tokens ("slow", "lost", "fail", "returnfail", …) via
// adapter config rather than constructing Guids that contain reserved substrings.
func effectiveOrderID(orderID string, r *http.Request) string {
	if s := strings.TrimSpace(r.Header.Get("X-Carrier-Test-Scenario")); s != "" {
		return orderID + s
	}
	return orderID
}

// outboundFinalizeAt computes when an outbound shipment first advances. "lost" parcels
// never advance (zero time -> NULL timer); "slow" parcels stretch the delay ×10.
func outboundFinalizeAt(now time.Time, orderID string, base time.Duration) time.Time {
	if domain.IsLost(orderID) {
		return time.Time{}
	}
	delay := base
	if domain.IsSlow(orderID) {
		delay = base * 10
	}
	return now.Add(delay)
}
