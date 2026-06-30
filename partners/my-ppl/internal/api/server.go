package api

import (
	"encoding/json"
	"log"
	"net/http"
	"strings"

	"my-ppl/internal/config"
	"my-ppl/internal/store"
)

type Server struct {
	cfg    config.Config
	store  *store.Store
	logger *log.Logger
}

func NewServer(cfg config.Config, st *store.Store, logger *log.Logger) *Server {
	return &Server{cfg: cfg, store: st, logger: logger}
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()

	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "carrier": "ppl", "test_mode": true})
	})

	// Outbound two-phase booking. The literal "tracking" / "returns" segments and the
	// differing segment counts keep these patterns unambiguous under Go 1.22's ServeMux.
	mux.Handle("POST /api/v1/parcels", s.auth(http.HandlerFunc(s.handleCreateParcel)))
	mux.Handle("GET /api/v1/parcels/tracking/{trackingNumber}", s.auth(http.HandlerFunc(s.handleTracking)))
	mux.Handle("POST /api/v1/parcels/{id}/cancel", s.auth(http.HandlerFunc(s.handleCancel)))
	mux.Handle("GET /api/v1/parcels/{referenceId}", s.auth(http.HandlerFunc(s.handlePoll)))

	// Returns.
	mux.Handle("POST /api/v1/parcels/returns", s.auth(http.HandlerFunc(s.handleCreateReturn)))
	mux.Handle("POST /api/v1/parcels/returns/{id}/cancel", s.auth(http.HandlerFunc(s.handleCancelReturn)))

	// Webhook registration.
	mux.Handle("POST /api/v1/webhooks", s.auth(http.HandlerFunc(s.handleRegisterWebhook)))

	return s.recover(s.logRequests(mux))
}

func (s *Server) auth(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		header := r.Header.Get("Authorization")
		const prefix = "Bearer "
		if !strings.HasPrefix(header, prefix) || strings.TrimPrefix(header, prefix) != s.cfg.APIKey {
			writeJSON(w, http.StatusUnauthorized, errorResponse{
				Error:   "unauthorized",
				Message: "Missing or invalid API key",
			})
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (s *Server) recover(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if rec := recover(); rec != nil {
				s.logger.Printf("[api] panic recovered: %v", rec)
				writeJSON(w, http.StatusInternalServerError, errorResponse{
					Error:   "internal_error",
					Message: "Unexpected sandbox error",
				})
			}
		}()
		next.ServeHTTP(w, r)
	})
}

func (s *Server) logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		s.logger.Printf("[api] %s %s", r.Method, r.URL.Path)
		next.ServeHTTP(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

func decodeJSON(r *http.Request, dst any) error {
	defer r.Body.Close()
	return json.NewDecoder(r.Body).Decode(dst)
}
