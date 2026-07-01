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
	// plain shared secret sent as X-PPL-Webhook-Secret on outbound webhooks (no HMAC)
	WebhookSecret string
	// SQLite database file path; survives restarts so parked return timers are not lost
	DBPath string
	// how often the background loop ticks
	WorkerInterval time.Duration
	// how long the "pending" booking-acceptance poll phase lasts
	BookingAcceptDelay time.Duration
	// gap between progressive webhook events (in_transit -> out_for_delivery -> delivered)
	EventSpacing time.Duration
	// delay before the return webhook chain starts (simulates the customer visiting a ParcelShop)
	ReturnQrScanWindow time.Duration
	// retries before a webhook event is marked dead
	WebhookMaxAttempts int
	// base delay for exponential webhook retry backoff
	WebhookBaseDelay time.Duration
}

func Load() Config {
	cfg := Config{
		Port:               getEnv("PORT", "8092"),
		APIKey:             getEnv("API_KEY", "ppl_sandbox_key"),
		WebhookSecret:      getEnv("WEBHOOK_SECRET", "dev_ppl_secret"),
		DBPath:             getEnv("DB_PATH", "./ppl.db"),
		WorkerInterval:     getEnvDuration("WORKER_INTERVAL", time.Second),
		BookingAcceptDelay: getEnvDuration("BOOKING_ACCEPT_DELAY", 3*time.Second),
		EventSpacing:       getEnvDuration("EVENT_SPACING", 2*time.Second),
		ReturnQrScanWindow: getEnvDuration("RETURN_QR_SCAN_WINDOW", 5*time.Second),
		WebhookMaxAttempts: getEnvInt("WEBHOOK_MAX_ATTEMPTS", 8),
		WebhookBaseDelay:   getEnvDuration("WEBHOOK_BASE_DELAY", 2*time.Second),
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
