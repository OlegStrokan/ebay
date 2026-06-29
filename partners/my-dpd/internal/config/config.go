package config

import (
	"log"
	"os"
	"strconv"
	"time"
)

type Config struct {
	// HTTP listen port
	Port string
	// bearer token the Order service must present on every request
	APIKey string
	// shared HMAC secret used to sign outbound webhooks (Stripe-Signature scheme)
	WebhookSecret string
	// SQLite database file path; survives restarts so parked return timers are not lost
	DBPath string
	// how often the background loop ticks
	WorkerInterval time.Duration
	// time from outbound create to the first status advance (created -> in_transit)
	ShipmentFinalizeDelay time.Duration
	// time from return create to return.delivered
	ReturnFinalizeDelay time.Duration
	// gap between the two outbound transitions (in_transit -> delivered)
	StatusAdvanceGap time.Duration
	// retries before a webhook event is marked dead
	WebhookMaxAttempts int
	// base delay for exponential webhook retry backoff
	WebhookBaseDelay time.Duration
}

func Load() Config {
	cfg := Config{
		Port:                  getEnv("PORT", "8091"),
		APIKey:                getEnv("API_KEY", "dpd_sandbox_key"),
		WebhookSecret:         getEnv("WEBHOOK_SECRET", "dev_dpd_secret"),
		DBPath:                getEnv("DB_PATH", "./dpd.db"),
		WorkerInterval:        getEnvDuration("WORKER_INTERVAL", time.Second),
		ShipmentFinalizeDelay: getEnvDuration("SHIPMENT_FINALIZE_DELAY", 5*time.Second),
		ReturnFinalizeDelay:   getEnvDuration("RETURN_FINALIZE_DELAY", 10*time.Second),
		StatusAdvanceGap:      getEnvDuration("STATUS_ADVANCE_GAP", 2*time.Second),
		WebhookMaxAttempts:    getEnvInt("WEBHOOK_MAX_ATTEMPTS", 8),
		WebhookBaseDelay:      getEnvDuration("WEBHOOK_BASE_DELAY", 2*time.Second),
	}

	if cfg.WebhookSecret == "" {
		log.Println("[config] WARNING: WEBHOOK_SECRET is empty; outbound webhooks will be rejected by the Gateway")
	}

	return cfg
}

func getEnv(key, fallback string) string {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		return v
	}
	return fallback
}

func getEnvInt(key string, fallback int) int {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		if parsed, err := strconv.Atoi(v); err == nil {
			return parsed
		}
		log.Printf("[config] invalid int for %s=%q, using default %d", key, v, fallback)
	}
	return fallback
}

func getEnvDuration(key string, fallback time.Duration) time.Duration {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		if parsed, err := time.ParseDuration(v); err == nil {
			return parsed
		}
		log.Printf("[config] invalid duration for %s=%q, using default %s", key, v, fallback)
	}
	return fallback
}
