package config

import (
	"log"
	"os"
	"strconv"
	"time"
)

type Config struct {
	Port string
	// api key of payment-service presents on every reqeuest
	APIKey string
	// shared HMAC secret used to sign outbound webhooks
	WebhookSecret string
	// payment service webhook which we trigger
	WebhookURL string
	// how long a "pending" | "requires_action" intent/refund stays
	// unsettled before worker finalizes and emit to webhook
	FinalizeDelay time.Duration
	// retries before an event is marked dead
	WebhookMaxAttempts int
	// exponential backoff between retries
	WebhookBaseDelay time.Duration
	// how often background loop ticks
	WorkerInterval time.Duration
	// how long "requires_capture" stays valid before it auto-expires and can no
	// longer be captured. mirrors real card netowrk +- 7 days
	AuthHoldTTL time.Duration
}

func Load() Config {
	cfg := Config{
		Port: getEnv("PORT", "8080"),
		APIKey: getEnv("API_KEY", "sandbox_test_key"),
		WebhookSecret: getEnv("WEBHOOK_SECRET", "dev_webhook_secret"),
		WebhookURL: getEnv("WEBHOOK_URL", "http://localhost:8084/api/v1/webhooks/stripe"),
		FinalizeDelay: getEnvDuration("FINALIZE_DELAY", 3*time.Second),
		WebhookMaxAttempts: getEnvInt("WEBHOOK_MAX_ATTEMPTS", 8),
		WebhookBaseDelay: getEnvDuration("WORKER_BASE_DELAY", 2*time.Second),
		WorkerInterval: getEnvDuration("WORKET_INTERVAL", time.Second),
		AuthHoldTTL: getEnvDuration("AUTH_HOLD_TTL", 8*24*time.Hour),
	}

	if cfg.WebhookSecret == "" {
		log.Println("[config] WARNING: WEBHOOK_SECRET is empty; outboud webhooks will be rejected by the payment-service")
	}

	return cfg
}


func getEnv(key, fallback string) string {
	v, ok := os.LookupEnv(key)
	if ok && v != "" {
    	return v
	}
	return fallback
}

func getEnvInt(key string, fallback int) int {
	v, ok := os.LookupEnv(key)
	if ok && v != "" {
		if parsed, err := strconv.Atoi(v); err == nil {
			return parsed
		}
		log.Printf("[config] invalid int for %s=%q, using default %d", key, v, fallback)
	}
	return fallback
}

func getEnvDuration(key string, fallback time.Duration) time.Duration {
	v, ok := os.LookupEnv(key)
	if ok && v != "" {
		if parsed, err := time.ParseDuration(v); err == nil {
			return parsed
		}
		log.Printf("[config] invalid duration for %s=%q, using default %s", key, v, fallback)
	}
	return fallback
}