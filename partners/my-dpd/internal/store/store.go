// SQLite-backed persistence layer. Unlike my-stripe (in-memory), the carrier must
// survive restarts: a return saga parks for the duration of the QR/pickup window and
// must not lose its registered webhook or pending timer. A single connection plus a
// mutex serialises all access, which is plenty for a deterministic sandbox.
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

	StatusCreated        = "created"
	StatusInTransit      = "in_transit"
	StatusDelivered      = "delivered"
	StatusCancelled      = "cancelled"
	StatusAwaitingPickup = "awaiting_pickup"
)

// Shipment is a row in the shipments table (outbound or return).
type Shipment struct {
	ID                 string
	Kind               string
	OrderID            string
	CustomerID         string
	TrackingNumber     string
	PostalCode         string
	Status             string
	FinalizeAt         time.Time // zero value = no advance scheduled
	ExpectedPickupDate time.Time
	CreatedAt          time.Time
	UpdatedAt          time.Time
}

// WebhookRegistration is a callback the Gateway asked us to fire for a shipment.
type WebhookRegistration struct {
	ID          string
	ShipmentID  string
	CallbackURL string
	Events      []string
	CreatedAt   time.Time
}

// WebhookEvent is a queued outbound delivery attempt (transactional outbox).
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
	// SQLite tolerates a single writer; serialise access to one connection.
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

func (s *Store) PutShipment(sh *Shipment) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := s.db.Exec(`
		INSERT INTO shipments
			(id, kind, order_id, customer_id, tracking_number, postal_code, status,
			 finalize_at, expected_pickup_date, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		sh.ID, sh.Kind, sh.OrderID, nullString(sh.CustomerID), sh.TrackingNumber,
		nullString(sh.PostalCode), sh.Status, toMillis(sh.FinalizeAt),
		toMillis(sh.ExpectedPickupDate), sh.CreatedAt.UnixMilli(), sh.UpdatedAt.UnixMilli())
	return err
}

func (s *Store) GetShipment(id string) (Shipment, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.getShipmentLocked(id)
}

func (s *Store) getShipmentLocked(id string) (Shipment, bool, error) {
	row := s.db.QueryRow(`
		SELECT id, kind, order_id, customer_id, tracking_number, postal_code, status,
		       finalize_at, expected_pickup_date, created_at, updated_at
		FROM shipments WHERE id = ?`, id)
	sh, err := scanShipment(row)
	if errors.Is(err, sql.ErrNoRows) {
		return Shipment{}, false, nil
	}
	if err != nil {
		return Shipment{}, false, err
	}
	return sh, true, nil
}

// UpdateShipmentStatus sets a new status and finalize timer; returns false if the
// shipment does not exist.
func (s *Store) UpdateShipmentStatus(id, status string, finalizeAt time.Time) (Shipment, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	res, err := s.db.Exec(`UPDATE shipments SET status = ?, finalize_at = ?, updated_at = ? WHERE id = ?`,
		status, toMillis(finalizeAt), time.Now().UTC().UnixMilli(), id)
	if err != nil {
		return Shipment{}, false, err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return Shipment{}, false, nil
	}
	return s.getShipmentLocked(id)
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

// AdvanceDueShipments moves every shipment whose timer has elapsed one step along its
// lifecycle and returns those that just reached "delivered" so the caller can fire
// webhooks. Outbound parcels go created -> in_transit -> delivered (two ticks, gap
// apart); returns go awaiting_pickup -> delivered in a single step.
func (s *Store) AdvanceDueShipments(now time.Time, gap time.Duration) ([]Shipment, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	rows, err := s.db.Query(`
		SELECT id, kind, order_id, customer_id, tracking_number, postal_code, status,
		       finalize_at, expected_pickup_date, created_at, updated_at
		FROM shipments
		WHERE finalize_at IS NOT NULL AND finalize_at <= ? AND status NOT IN (?, ?)`,
		now.UnixMilli(), StatusDelivered, StatusCancelled)
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

	var delivered []Shipment
	for _, sh := range due {
		var newStatus string
		var newFinalize sql.NullInt64
		becameDelivered := false

		switch {
		case sh.Kind == KindOutbound && sh.Status == StatusCreated:
			newStatus = StatusInTransit
			newFinalize = sql.NullInt64{Int64: now.Add(gap).UnixMilli(), Valid: true}
		case sh.Kind == KindOutbound && sh.Status == StatusInTransit:
			newStatus = StatusDelivered
			becameDelivered = true
		case sh.Kind == KindReturn && sh.Status == StatusAwaitingPickup:
			newStatus = StatusDelivered
			becameDelivered = true
		default:
			continue
		}

		if _, err := s.db.Exec(`UPDATE shipments SET status = ?, finalize_at = ?, updated_at = ? WHERE id = ?`,
			newStatus, newFinalize, now.UnixMilli(), sh.ID); err != nil {
			return nil, err
		}

		if becameDelivered {
			sh.Status = newStatus
			sh.FinalizeAt = time.Time{}
			delivered = append(delivered, sh)
		}
	}
	return delivered, nil
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
		sh         Shipment
		customerID sql.NullString
		postalCode sql.NullString
		finalizeAt sql.NullInt64
		expectedAt sql.NullInt64
		createdAt  int64
		updatedAt  int64
	)
	if err := sc.Scan(&sh.ID, &sh.Kind, &sh.OrderID, &customerID, &sh.TrackingNumber,
		&postalCode, &sh.Status, &finalizeAt, &expectedAt, &createdAt, &updatedAt); err != nil {
		return Shipment{}, err
	}
	sh.CustomerID = customerID.String
	sh.PostalCode = postalCode.String
	sh.FinalizeAt = fromMillis(finalizeAt)
	sh.ExpectedPickupDate = fromMillis(expectedAt)
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
