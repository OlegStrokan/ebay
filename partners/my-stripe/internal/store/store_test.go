package store

import (
	"testing"
	"time"
)

func TestSaveIdempotent_ThenIdempotent_ReturnsCachedBody(t *testing.T) {
	s := New()
	s.SaveIdempotent("capture", "key-1", []byte("cached-response"))

	body, ok := s.Idempotent("capture", "key-1")
	if !ok {
		t.Fatal("expected cached body to be found")
	}
	if string(body) != "cached-response" {
		t.Fatalf("got %q, want %q", body, "cached-response")
	}
}

func TestIdempotent_MissingKey_ReturnsNotOk(t *testing.T) {
	s := New()
	if _, ok := s.Idempotent("capture", "missing"); ok {
		t.Fatal("expected missing key to not be found")
	}
}

func TestEvictExpiredIdempotency_RemovesOnlyEntriesOlderThanTTL(t *testing.T) {
	s := New()
	now := time.Now().UTC()
	ttl := time.Hour

	s.SaveIdempotent("capture", "old", []byte("stale"))
	s.idem["capture:old"] = idempotentEntry{body: []byte("stale"), savedAt: now.Add(-2 * ttl)}

	s.SaveIdempotent("capture", "fresh", []byte("fresh"))

	removed := s.EvictExpiredIdempotency(now, ttl)
	if removed != 1 {
		t.Fatalf("got %d removed, want 1", removed)
	}

	if _, ok := s.Idempotent("capture", "old"); ok {
		t.Fatal("expected expired key to be evicted")
	}
	if _, ok := s.Idempotent("capture", "fresh"); !ok {
		t.Fatal("expected fresh key to survive eviction")
	}
}

func TestEvictExpiredIdempotency_NothingExpired_ReturnsZero(t *testing.T) {
	s := New()
	s.SaveIdempotent("capture", "fresh", []byte("fresh"))

	removed := s.EvictExpiredIdempotency(time.Now().UTC(), time.Hour)
	if removed != 0 {
		t.Fatalf("got %d removed, want 0", removed)
	}
}
