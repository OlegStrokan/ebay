// SQLite-backed persistence layer for the fake PPL carrier. Like my-dpd it must
// survive restarts so a parked return (waiting on its QR-scan window) does not lose
// its timer or registered webhook. A single connection plus a mutex serialises access.
package store

import (
	"database/sql"
	_ "embed"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	_ "modernc.org/sqlite"
)

//go:embed schema.sql
var schemaSQL string

const (
	KindOutbound = "outbound"
	KindReturn   = "return"

	StatusPending        = "pending"
	StatusAccepted       = "accepted"
	StatusInTransit      = "in_transit"
	StatusOutForDelivery = "out_for_delivery"
	StatusDelivered      = "delivered"
	StatusCancelled      = "cancelled"
	StatusRejected       = "rejected"
	StatusAwaitingScan   = "awaiting_scan"
)

// Shipment is a row in the shipments table (outbound booking or return).
type Shipment struct {
	ID                 string // reference_id (outbound) | return_shipment_id (return)
	Kind               string
	OrderID            string
	CustomerID         string
	PostalCode         string
	ParcelID           string
	TrackingNumber     string
	QrToken            string
	PollOutcome        string
	AcceptAt           time.Time
	Status             string
	NextEventAt        time.Time
	ExpectedPickupDate time.Time
	CancelBlock        bool
	CreatedAt          time.Time
	UpdatedAt          time.Time
}

// PublicID is the identifier the Gateway registers webhooks against and that the
// worker matches registrations on: the parcel id for outbound, the row id for returns.
func (s Shipment) PublicID() string {
	if s.Kind == KindOutbound && s.ParcelID != "" {
		return s.ParcelID
	}
	return s.ID
}

type WebhookRegistration struct {
	ID          string
	ShipmentID  string
	CallbackURL string
	Events      []string
	CreatedAt   time.Time
}

type WebhookEvent struct {
	ID            string
	ShipmentID    string
	EventType     string
	CallbackURL   string
	Body          []byte
	Attempts      int
	NextAttemptAt time.Time
	Delivered     bool
	Dead          bool
}

// Transition is a shipment that just advanced one progressive stage and the event the
// worker should fire for it.
type Transition struct {
	Shipment  Shipment
	EventType string
}

type Store struct {
	mu sync.Mutex
	db *sql.DB
}

func New(dbPath string) (*Store, error) {
	if !isMemoryPath(dbPath) {
		if dir := filepath.Dir(dbPath); dir != "" && dir != "." {
			if err := os.MkdirAll(dir, 0o755); err != nil {
				return nil, err
			}
		}
	}

	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)

	if _, err := db.Exec(`PRAGMA busy_timeout=5000;`); err != nil {
		db.Close()
		return nil, err
	}
	if _, err := db.Exec(schemaSQL); err != nil {
		db.Close()
		return nil, err
	}

	return &Store{db: db}, nil
}

func (s *Store) Close() error {
	return s.db.Close()
}

const shipmentColumns = `id, kind, order_id, customer_id, postal_code, parcel_id, tracking_number,
	qr_token, poll_outcome, accept_at, status, next_event_at, expected_pickup_date,
	cancel_block, created_at, updated_at`

func (s *Store) PutShipment(sh *Shipment) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`
		INSERT INTO shipments (`+shipmentColumns+`)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		sh.ID, sh.Kind, sh.OrderID, nullString(sh.CustomerID), nullString(sh.PostalCode),
		nullString(sh.ParcelID), nullString(sh.TrackingNumber), nullString(sh.QrToken),
		nullString(sh.PollOutcome), toMillis(sh.AcceptAt), sh.Status, toMillis(sh.NextEventAt),
		toMillis(sh.ExpectedPickupDate), boolToInt(sh.CancelBlock),
		sh.CreatedAt.UnixMilli(), sh.UpdatedAt.UnixMilli())
	return err
}

func (s *Store) GetShipment(id string) (Shipment, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.queryOneLocked(`SELECT `+shipmentColumns+` FROM shipments WHERE id = ?`, id)
}

func (s *Store) GetByParcelID(parcelID string) (Shipment, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.queryOneLocked(`SELECT `+shipmentColumns+` FROM shipments WHERE parcel_id = ?`, parcelID)
}

func (s *Store) GetByTracking(tracking string) (Shipment, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.queryOneLocked(`SELECT `+shipmentColumns+` FROM shipments WHERE tracking_number = ?`, tracking)
}

func (s *Store) queryOneLocked(query string, args ...any) (Shipment, bool, error) {
	sh, err := scanShipment(s.db.QueryRow(query, args...))
	if errors.Is(err, sql.ErrNoRows) {
		return Shipment{}, false, nil
	}
	if err != nil {
		return Shipment{}, false, err
	}
	return sh, true, nil
}

// MarkAccepted assigns parcel identifiers and moves a pending booking into its delivery
// lifecycle. Used by the lazy accept performed inside the GET poll handler.
func (s *Store) MarkAccepted(id, parcelID, tracking, status string, nextEventAt time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`
		UPDATE shipments
		SET parcel_id = ?, tracking_number = ?, status = ?, next_event_at = ?, accept_at = NULL, updated_at = ?
		WHERE id = ?`,
		parcelID, tracking, status, toMillis(nextEventAt), time.Now().UTC().UnixMilli(), id)
	return err
}

func (s *Store) MarkRejected(id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`UPDATE shipments SET status = ?, accept_at = NULL, updated_at = ? WHERE id = ?`,
		StatusRejected, time.Now().UTC().UnixMilli(), id)
	return err
}

// SetStatus updates a shipment's status and progressive timer (used by cancel).
func (s *Store) SetStatus(id, status string, nextEventAt time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`UPDATE shipments SET status = ?, next_event_at = ?, updated_at = ? WHERE id = ?`,
		status, toMillis(nextEventAt), time.Now().UTC().UnixMilli(), id)
	return err
}

func (s *Store) PutRegistration(r *WebhookRegistration) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	events, err := json.Marshal(r.Events)
	if err != nil {
		return err
	}
	_, err = s.db.Exec(`
		INSERT INTO webhook_registrations (id, shipment_id, callback_url, events, created_at)
		VALUES (?, ?, ?, ?, ?)`,
		r.ID, r.ShipmentID, r.CallbackURL, string(events), r.CreatedAt.UnixMilli())
	return err
}

func (s *Store) RegistrationsForShipment(shipmentID string) ([]WebhookRegistration, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	rows, err := s.db.Query(`
		SELECT id, shipment_id, callback_url, events, created_at
		FROM webhook_registrations WHERE shipment_id = ?`, shipmentID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var out []WebhookRegistration
	for rows.Next() {
		var (
			r         WebhookRegistration
			events    string
			createdAt int64
		)
		if err := rows.Scan(&r.ID, &r.ShipmentID, &r.CallbackURL, &events, &createdAt); err != nil {
			return nil, err
		}
		_ = json.Unmarshal([]byte(events), &r.Events)
		r.CreatedAt = time.UnixMilli(createdAt).UTC()
		out = append(out, r)
	}
	return out, rows.Err()
}

func (s *Store) EnqueueEvent(ev *WebhookEvent) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`
		INSERT INTO webhook_events
			(id, shipment_id, event_type, callback_url, body, attempts, next_attempt_at, delivered, dead, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		ev.ID, ev.ShipmentID, ev.EventType, ev.CallbackURL, ev.Body, ev.Attempts,
		ev.NextAttemptAt.UnixMilli(), boolToInt(ev.Delivered), boolToInt(ev.Dead),
		time.Now().UTC().UnixMilli())
	return err
}

// AdvanceDueEvents moves every shipment whose progressive timer has elapsed one stage
// forward and returns the transition (with the event type to fire). Outbound parcels
// run accepted -> in_transit -> out_for_delivery -> delivered; returns run
// awaiting_scan -> in_transit -> out_for_delivery -> delivered. Each non-final stage
// schedules the next event one EventSpacing later.
func (s *Store) AdvanceDueEvents(now time.Time, spacing time.Duration) ([]Transition, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	rows, err := s.db.Query(`
		SELECT `+shipmentColumns+`
		FROM shipments
		WHERE next_event_at IS NOT NULL AND next_event_at <= ?
		  AND status IN (?, ?, ?, ?)`,
		now.UnixMilli(), StatusAccepted, StatusInTransit, StatusOutForDelivery, StatusAwaitingScan)
	if err != nil {
		return nil, err
	}

	var due []Shipment
	for rows.Next() {
		sh, scanErr := scanShipment(rows)
		if scanErr != nil {
			rows.Close()
			return nil, scanErr
		}
		due = append(due, sh)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return nil, err
	}
	rows.Close()

	var transitions []Transition
	for _, sh := range due {
		newStatus, eventType, hasMore, ok := nextStage(sh.Kind, sh.Status)
		if !ok {
			continue
		}

		var nextEvent sql.NullInt64
		if hasMore {
			nextEvent = sql.NullInt64{Int64: now.Add(spacing).UnixMilli(), Valid: true}
		}
		if _, err := s.db.Exec(`UPDATE shipments SET status = ?, next_event_at = ?, updated_at = ? WHERE id = ?`,
			newStatus, nextEvent, now.UnixMilli(), sh.ID); err != nil {
			return nil, err
		}

		sh.Status = newStatus
		if !hasMore {
			sh.NextEventAt = time.Time{}
		}
		transitions = append(transitions, Transition{Shipment: sh, EventType: eventType})
	}
	return transitions, nil
}

func nextStage(kind, current string) (newStatus, eventType string, hasMore, ok bool) {
	prefix := "parcel"
	if kind == KindReturn {
		prefix = "return"
	}
	switch current {
	case StatusAccepted, StatusAwaitingScan:
		return StatusInTransit, prefix + ".in_transit", true, true
	case StatusInTransit:
		return StatusOutForDelivery, prefix + ".out_for_delivery", true, true
	case StatusOutForDelivery:
		return StatusDelivered, prefix + ".delivered", false, true
	default:
		return "", "", false, false
	}
}

func (s *Store) TakeDueEvents(now time.Time, max int) ([]WebhookEvent, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	rows, err := s.db.Query(`
		SELECT id, shipment_id, event_type, callback_url, body, attempts, next_attempt_at, delivered, dead
		FROM webhook_events
		WHERE delivered = 0 AND dead = 0 AND next_attempt_at <= ?
		ORDER BY next_attempt_at ASC
		LIMIT ?`, now.UnixMilli(), max)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var out []WebhookEvent
	for rows.Next() {
		var (
			ev              WebhookEvent
			nextMs          int64
			delivered, dead int
		)
		if err := rows.Scan(&ev.ID, &ev.ShipmentID, &ev.EventType, &ev.CallbackURL, &ev.Body,
			&ev.Attempts, &nextMs, &delivered, &dead); err != nil {
			return nil, err
		}
		ev.NextAttemptAt = time.UnixMilli(nextMs).UTC()
		ev.Delivered = delivered != 0
		ev.Dead = dead != 0
		out = append(out, ev)
	}
	return out, rows.Err()
}

func (s *Store) MarkEventDelivered(id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`UPDATE webhook_events SET delivered = 1, attempts = attempts + 1 WHERE id = ?`, id)
	return err
}

func (s *Store) MarkEventRetry(id string, next time.Time, dead bool) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`UPDATE webhook_events SET attempts = attempts + 1, next_attempt_at = ?, dead = ? WHERE id = ?`,
		next.UnixMilli(), boolToInt(dead), id)
	return err
}

// helpers

type rowScanner interface {
	Scan(dest ...any) error
}

func scanShipment(sc rowScanner) (Shipment, error) {
	var (
		sh             Shipment
		customerID     sql.NullString
		postalCode     sql.NullString
		parcelID       sql.NullString
		trackingNumber sql.NullString
		qrToken        sql.NullString
		pollOutcome    sql.NullString
		acceptAt       sql.NullInt64
		nextEventAt    sql.NullInt64
		expectedAt     sql.NullInt64
		cancelBlock    int
		createdAt      int64
		updatedAt      int64
	)
	if err := sc.Scan(&sh.ID, &sh.Kind, &sh.OrderID, &customerID, &postalCode, &parcelID,
		&trackingNumber, &qrToken, &pollOutcome, &acceptAt, &sh.Status, &nextEventAt,
		&expectedAt, &cancelBlock, &createdAt, &updatedAt); err != nil {
		return Shipment{}, err
	}
	sh.CustomerID = customerID.String
	sh.PostalCode = postalCode.String
	sh.ParcelID = parcelID.String
	sh.TrackingNumber = trackingNumber.String
	sh.QrToken = qrToken.String
	sh.PollOutcome = pollOutcome.String
	sh.AcceptAt = fromMillis(acceptAt)
	sh.NextEventAt = fromMillis(nextEventAt)
	sh.ExpectedPickupDate = fromMillis(expectedAt)
	sh.CancelBlock = cancelBlock != 0
	sh.CreatedAt = time.UnixMilli(createdAt).UTC()
	sh.UpdatedAt = time.UnixMilli(updatedAt).UTC()
	return sh, nil
}

func toMillis(t time.Time) sql.NullInt64 {
	if t.IsZero() {
		return sql.NullInt64{}
	}
	return sql.NullInt64{Int64: t.UnixMilli(), Valid: true}
}

func fromMillis(n sql.NullInt64) time.Time {
	if !n.Valid {
		return time.Time{}
	}
	return time.UnixMilli(n.Int64).UTC()
}

func nullString(v string) sql.NullString {
	if v == "" {
		return sql.NullString{}
	}
	return sql.NullString{String: v, Valid: true}
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func isMemoryPath(path string) bool {
	return path == ":memory:" || strings.Contains(path, ":memory:")
}
