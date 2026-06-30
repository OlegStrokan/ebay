package api

import (
	"net/http"
	"strings"
	"time"

	"my-ppl/internal/domain"
	"my-ppl/internal/store"
)

// handleCreateParcel is phase one of the two-phase booking: it validates synchronously
// and parks the booking as "pending" with a deferred accept/reject decision. The adapter
// then polls handlePoll until the depot settles the booking.
func (s *Server) handleCreateParcel(w http.ResponseWriter, r *http.Request) {
	var req createParcelRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	oid := effectiveOrderID(req.OrderID, r)

	switch domain.DecideCreateOutcome(oid, req.Address.PostalCode) {
	case domain.CreateServerError:
		writeJSON(w, http.StatusInternalServerError, errorResponse{"carrier_unavailable", "Simulated PPL booking-gateway outage"})
		return
	case domain.CreateInvalidAddress:
		// 422 triggers InvalidAddressException in the adapter -> saga compensation.
		writeJSON(w, http.StatusUnprocessableEntity, errorResponse{"invalid_address", "Recipient address could not be validated by PPL"})
		return
	}

	now := time.Now().UTC()
	acceptDelay := s.cfg.BookingAcceptDelay
	if domain.IsSlowPoll(oid) {
		acceptDelay *= 5
	}

	shipment := &store.Shipment{
		ID:          domain.NewID("ppl-ref"),
		Kind:        store.KindOutbound,
		OrderID:     req.OrderID,
		PostalCode:  req.Address.PostalCode,
		PollOutcome: string(domain.DecidePollOutcome(oid, req.Address.PostalCode)),
		AcceptAt:    now.Add(acceptDelay),
		Status:      store.StatusPending,
		CancelBlock: domain.IsCancelBlock(oid),
		CreatedAt:   now,
		UpdatedAt:   now,
	}
	if err := s.store.PutShipment(shipment); err != nil {
		s.logger.Printf("[api] failed to persist booking: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to persist booking"})
		return
	}

	writeJSON(w, http.StatusAccepted, createParcelResponse{
		ReferenceID: shipment.ID,
		Status:      store.StatusPending,
		TestMode:    true,
	})
}

// handlePoll is phase two: it reports pending until the accept timer elapses, then
// settles the booking as accepted (assigning parcel + tracking ids) or rejected.
func (s *Server) handlePoll(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("referenceId")
	sh, ok, err := s.store.GetShipment(id)
	if err != nil {
		s.logger.Printf("[api] poll lookup failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to read booking"})
		return
	}
	if !ok {
		writeJSON(w, http.StatusNotFound, errorResponse{"not_found", "Unknown reference id"})
		return
	}

	now := time.Now().UTC()

	switch sh.Status {
	case store.StatusPending:
		if now.Before(sh.AcceptAt) {
			writeJSON(w, http.StatusOK, pollResponse{ReferenceID: sh.ID, Status: store.StatusPending, TestMode: true})
			return
		}
		if sh.PollOutcome == string(domain.PollRejected) {
			if err := s.store.MarkRejected(sh.ID); err != nil {
				s.logger.Printf("[api] mark rejected failed: %v", err)
			}
			writeJSON(w, http.StatusOK, pollResponse{
				ReferenceID: sh.ID, Status: store.StatusRejected,
				Reason: "depot capacity exceeded", TestMode: true,
			})
			return
		}
		// Accept: assign parcel identifiers and enter the delivery lifecycle. A
		// "cancelblock" parcel goes straight into the network so a later cancel is refused.
		parcelID := domain.NewID("ppl-shp")
		tracking := domain.NewID("ppl-trk")
		status := store.StatusAccepted
		if sh.CancelBlock {
			status = store.StatusInTransit
		}
		nextEvent := now.Add(s.cfg.EventSpacing)
		if err := s.store.MarkAccepted(sh.ID, parcelID, tracking, status, nextEvent); err != nil {
			s.logger.Printf("[api] mark accepted failed: %v", err)
			writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to accept booking"})
			return
		}
		writeJSON(w, http.StatusOK, pollResponse{
			ReferenceID: sh.ID, Status: store.StatusAccepted,
			ParcelID: parcelID, TrackingNumber: tracking, TestMode: true,
		})

	case store.StatusRejected:
		writeJSON(w, http.StatusOK, pollResponse{
			ReferenceID: sh.ID, Status: store.StatusRejected,
			Reason: "depot capacity exceeded", TestMode: true,
		})

	default:
		// Already accepted (or further along) — return the assigned ids idempotently.
		writeJSON(w, http.StatusOK, pollResponse{
			ReferenceID: sh.ID, Status: store.StatusAccepted,
			ParcelID: sh.ParcelID, TrackingNumber: sh.TrackingNumber, TestMode: true,
		})
	}
}

// handleCancel refuses cancellation once the parcel is in the PPL network (409). This is
// the critical divergence from DPD: the adapter propagates the resulting error and the
// saga raises a manual-intervention ticket for that compensation step.
func (s *Server) handleCancel(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")

	sh, ok, err := s.lookupByParcelOrReference(id)
	if err != nil {
		s.logger.Printf("[api] cancel lookup failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to read parcel"})
		return
	}
	if !ok {
		writeJSON(w, http.StatusNotFound, errorResponse{"not_found", "Unknown parcel"})
		return
	}

	switch sh.Status {
	case store.StatusInTransit, store.StatusOutForDelivery, store.StatusDelivered:
		writeJSON(w, http.StatusConflict, errorResponse{
			"cannot_cancel_in_transit", "Parcel is already in the PPL network",
		})
		return
	}

	if err := s.store.SetStatus(sh.ID, store.StatusCancelled, time.Time{}); err != nil {
		s.logger.Printf("[api] cancel update failed: %v", err)
	}
	writeJSON(w, http.StatusOK, cancelResponse{ID: id, Status: store.StatusCancelled, TestMode: true})
}

func (s *Server) handleTracking(w http.ResponseWriter, r *http.Request) {
	tracking := r.PathValue("trackingNumber")
	sh, ok, err := s.store.GetByTracking(tracking)
	if err != nil {
		s.logger.Printf("[api] tracking lookup failed: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to read shipment"})
		return
	}
	if !ok {
		writeJSON(w, http.StatusNotFound, errorResponse{"not_found", "Unknown tracking number"})
		return
	}
	writeJSON(w, http.StatusOK, trackingResponse{TrackingNumber: tracking, Status: sh.Status, TestMode: true})
}

func (s *Server) handleRegisterWebhook(w http.ResponseWriter, r *http.Request) {
	var req registerWebhookRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}
	if req.ShipmentID == "" || req.CallbackURL == "" {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "shipmentId and callbackUrl are required"})
		return
	}

	reg := &store.WebhookRegistration{
		ID:          domain.NewID("ppl-whk"),
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

// handleCreateReturn parks a return as "awaiting_scan". The worker only begins the return
// event chain after the QR scan window elapses (simulating the customer visiting a ParcelShop).
func (s *Server) handleCreateReturn(w http.ResponseWriter, r *http.Request) {
	var req createReturnRequest
	if err := decodeJSON(r, &req); err != nil {
		writeJSON(w, http.StatusBadRequest, errorResponse{"invalid_request", "Malformed JSON body"})
		return
	}

	oid := effectiveOrderID(req.OrderID, r)

	if domain.DecideReturnOutcome(oid) == domain.CreateInvalidAddress {
		writeJSON(w, http.StatusUnprocessableEntity, errorResponse{"invalid_address", "Return pickup address could not be validated by PPL"})
		return
	}

	now := time.Now().UTC()
	expectedPickup := now.Add(48 * time.Hour)
	shipment := &store.Shipment{
		ID:                 domain.NewID("ppl-ret"),
		Kind:               store.KindReturn,
		OrderID:            req.OrderID,
		CustomerID:         req.CustomerID,
		TrackingNumber:     domain.NewID("ppl-rtrk"),
		QrToken:            domain.NewID("ppl-qr"),
		Status:             store.StatusAwaitingScan,
		NextEventAt:        now.Add(s.cfg.ReturnQrScanWindow),
		ExpectedPickupDate: expectedPickup,
		CreatedAt:          now,
		UpdatedAt:          now,
	}
	if err := s.store.PutShipment(shipment); err != nil {
		s.logger.Printf("[api] failed to persist return: %v", err)
		writeJSON(w, http.StatusInternalServerError, errorResponse{"internal_error", "Failed to persist return"})
		return
	}

	writeJSON(w, http.StatusCreated, createReturnResponse{
		ReturnShipmentID:     shipment.ID,
		ReturnTrackingNumber: shipment.TrackingNumber,
		ExpectedPickupDate:   expectedPickup.Format(time.RFC3339),
		QrToken:              shipment.QrToken,
		TestMode:             true,
	})
}

func (s *Server) handleCancelReturn(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if sh, ok, err := s.store.GetShipment(id); err == nil && ok && sh.Status != store.StatusDelivered {
		if err := s.store.SetStatus(id, store.StatusCancelled, time.Time{}); err != nil {
			s.logger.Printf("[api] cancel return update failed: %v", err)
		}
	}
	writeJSON(w, http.StatusOK, cancelResponse{ID: id, Status: store.StatusCancelled, TestMode: true})
}

// lookupByParcelOrReference resolves a cancel target by its parcel id, falling back to
// the booking reference id so cancellation works regardless of which identifier is sent.
func (s *Server) lookupByParcelOrReference(id string) (store.Shipment, bool, error) {
	if sh, ok, err := s.store.GetByParcelID(id); err != nil || ok {
		return sh, ok, err
	}
	return s.store.GetShipment(id)
}

// effectiveOrderID appends the X-Carrier-Test-Scenario header value to the orderId so
// that E2E tests can inject magic tokens ("slowpoll", "pollreject", "cancelblock",
// "returnfail", …) via adapter config rather than constructing Guids that contain
// reserved substrings.
func effectiveOrderID(orderID string, r *http.Request) string {
	if s := strings.TrimSpace(r.Header.Get("X-Carrier-Test-Scenario")); s != "" {
		return orderID + s
	}
	return orderID
}
