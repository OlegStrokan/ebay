package api

import (
	"encoding/json"
	"log"
	"net/http"
	"strings"

	"my-dpd/internal/config"
	"my-dpd/internal/store"
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
		writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "carrier": "dpd", "test_mode": true})
	})

	// Outbound shipments. The literal "/return" routes are 4 segments and never
	// collide with the 3-segment "{id}" routes under Go 1.22's ServeMux.
	mux.Handle("POST /v1/shipment", s.auth(http.HandlerFunc(s.handleCreateShipment)))
	mux.Handle("GET /v1/shipment/{id}/status", s.auth(http.HandlerFunc(s.handleGetStatus)))
	mux.Handle("DELETE /v1/shipment/{id}", s.auth(http.HandlerFunc(s.handleCancelShipment)))

	// Returns.
	mux.Handle("POST /v1/shipment/return", s.auth(http.HandlerFunc(s.handleCreateReturn)))
	mux.Handle("DELETE /v1/shipment/return/{id}", s.auth(http.HandlerFunc(s.handleCancelReturn)))

	// Webhook registration.
	mux.Handle("POST /v1/webhook", s.auth(http.HandlerFunc(s.handleRegisterWebhook)))

	return s.recover(s.logRequests(mux))
}

func (s *Server) auth(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		header := r.Header.Get("Authorization")
		const prefix = "Bearer "
		if !strings.HasPrefix(header, prefix) || strings.TrimPrefix(header, prefix) != s.cfg.APIKey {
			writeJSON(w, http.StatusUnauthorized, errorResponse{
				ErrorCode:    "unauthorized",
				ErrorMessage: "Missing or invalid API key",
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
					ErrorCode:    "internal_error",
					ErrorMessage: "Unexpected sandbox error",
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
