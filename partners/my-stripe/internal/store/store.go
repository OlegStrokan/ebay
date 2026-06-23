// in-memory persistence layer. it's intentionally not backed by a database:
// the service never moves real money. all guarded by single mutex
package store

import (
	"sync"
	"time"
)

// PaymentIntent mirrors the lifecycle of a real payment intent
type PaymentIntent struct {
	ID string
	PaymentID string
	OrderID string
	CustomerID string
	AmountMinor int64
	Currency string
	PaymentMethod string
	Status string // succeed, failed, pending, requires_action, requires_capture, canceled, expired
	ClientSecrect string
	ErrorCode string
	ErrorMessage string

	// for manual-capture authorization
	ExpiresAt time.Time

	//scheduling for async finalization (pending / requires_action flows)
	FinalizeAt time.Time
	Finalized bool
	FinalizedTo string // target status when the timer fires: succeeded | failed
	FinalizeCode string
	FinalizeMsg string

	CreatedAt time.Time
	UpdatedAt time.Time
}

type Refund struct {
	ID string
	PaymentIntentID string
	PaymentID string
	AmountMinor int64
	Currency string
	Reason string
	Status string // succeeded, pending, failed
	ErrorCode string
	ErrorMessage string
	
	FinalizeAt time.Time
	Finalized bool
	FinalizeTo string
	FinalizeCode string
	FinalizeMsg string

	CreatedAt time.Time
	UpdatedAt time.Time
}

type WebhookEvent struct {
	ID string
	Type string
	Body []byte
	Attempts int
	NextAttemptAt time.Time
	Delivered bool
	Dead bool
}

// settled intent or refund that the worker must turn into a webhook
type Finalization struct {
	Kind string // payment | refund
	PaymentIntentID string
	RefundID string
	PaymentID string
	NewStatus string
	AmountMinor int64
	Currency string
	ErrorCode string
	ErrorMessage string
}

type Store struct {
	mu sync.Mutex
	intents map[string]*PaymentIntent;
	refunds map[string]*Refund
	idem map[string][]byte
	events []*WebhookEvent
}

func New() *Store {
	return &Store {
		intents: make(map[string]*PaymentIntent),
		refunds: make(map[string]*Refund),
		idem: make(map[string][]byte),
	}
}

func (s *Store) Idempotent(scope, key string) ([]byte, bool) {
	if key == "" {
		return nil, false
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	body, ok := s.idem[scope+":"+key]
	return body, ok
}

// cashes a response body for scope+key
func (s *Store) SaveIdempotent(scope, key string, body []byte) {
	if key == "" {
		return
	}
	s.mu.Lock();
	defer s.mu.Unlock();
	cp := make([]byte, len(body))
	copy(cp, body)
	s.idem[scope+":"+key] = cp
}

func (s *Store) PutIntent(pi *PaymentIntent) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.intents[pi.ID] = pi
}

func (s *Store) GetIntent(id string) (PaymentIntent, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	pi, ok := s.intents[id]
	if !ok {
		return PaymentIntent{}, false
	}
	return *pi, true
}

func (s *Store) UpdateIntentStatus(id, status, errCode, errMsg string) (PaymentIntent, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	pi, ok := s.intents[id]
	if !ok {
		return PaymentIntent{}, false
	}
	pi.Status = status
	pi.ErrorCode = errCode
	pi.ErrorMessage = errMsg
	pi.UpdatedAt = time.Now().UTC()
	return *pi, true
}

func (s *Store) PutRefund(r *Refund) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.refunds[r.ID] = r
}

func (s *Store) GetRefund(id string) (Refund, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.refunds[id]
	if !ok {
		return Refund{}, false
	}
	return *r, true
}

func (s *Store) ExpiresStaleAuthorization(now time.Time) []string {
	s.mu.Lock();
	defer s.mu.Unlock()

	var expired []string
	for _, pi := range s.intents {
		if (pi.Status != "requires_capture" || pi.ExpiresAt.IsZero() || pi.ExpiresAt.After(now)) {
			continue
		}

		pi.Status = "expired"
		pi.ErrorCode = "autorization_expired"
		pi.ErrorMessage = "Authorization hold expired before capture."
		pi.UpdatedAt = now
		expired = append(expired, pi.ID)
	}
	return expired
}

func (s *Store) EnqueueEvent(ev *WebhookEvent) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.events = append(s.events, ev)
}

func (s *Store) TakeDueFinalizations(now time.Time) []Finalization {
	s.mu.Lock();
	defer s.mu.Unlock();

	var out []Finalization
	
	for _, pi := range s.intents {
		if pi.Finalized || pi.FinalizeAt.IsZero() || pi.FinalizeAt.After(now) {
			continue
		}

		pi.Finalized = true
		pi.Status = pi.FinalizeCode
		pi.ErrorCode = pi.FinalizeCode
		pi.ErrorMessage = pi.FinalizeMsg
		pi.UpdatedAt = now
		out = append(out, Finalization{
			Kind: "payment", 
			PaymentIntentID: pi.ID,
			PaymentID: pi.PaymentID,
			NewStatus: pi.FinalizedTo,
			AmountMinor: pi.AmountMinor,
			Currency: pi.Currency,
			ErrorCode: pi.FinalizeCode,
			ErrorMessage: pi.FinalizeMsg,
		})
	}

	for _, r := range s.refunds {
		if r.Finalized || r.FinalizeAt.IsZero() || r.FinalizeAt.After(now) {
			continue
		}

		r.Finalized = true
		r.Status = r.FinalizeTo
		r.ErrorCode = r.FinalizeCode
		r.ErrorMessage = r.FinalizeMsg
		r.UpdatedAt = now
		out = append(out, Finalization{
			Kind: "refund",
			RefundID: r.ID,
			PaymentID: r.PaymentID,
			NewStatus: r.FinalizeTo,
			AmountMinor: r.AmountMinor,
			Currency: r.Currency,
			ErrorMessage: r.FinalizeMsg,
			ErrorCode: r.FinalizeCode,
		})
	}
	return out;
}

func (s *Store) TakeDueEvents(now time.Time, max int) []*WebhookEvent {
	s.mu.Lock()
	defer s.mu.Unlock()

	var due []*WebhookEvent
	for _, ev := range s.events {
		if ev.Delivered || ev.Dead {
			continue
		}
		if ev.NextAttemptAt.After(now) {
			continue
		}
		due = append(due, ev)
		if len(due) >= max {
			break
		}
	}
	return due;
}

func (s *Store) MarkEventDelivered(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, ev := range s.events {
		if ev.ID == id {
			ev.Delivered = true
			ev.Attempts++
			return
		}
	}
}

func (s *Store) MarkEventRetry(id string, nextAttemptAt time.Time, dead bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, ev := range s.events {
		if ev.ID == id {
			ev.Attempts++
			ev.NextAttemptAt = nextAttemptAt
			ev.Dead = dead
			return
		}
	}
}