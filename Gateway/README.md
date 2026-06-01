# Gateway

The single entry point for all external HTTP traffic. Translates REST requests into gRPC calls against the backend services, handles JWT auth, and publishes user behavioural events to Kafka.

## Rate limiting

Some endpoints will burn you out fast if left unbounded — credential stuffing on `/login`, bots hammering `/password-reset/request`, scrapers blasting `/search`. The gateway uses ASP.NET Core's built-in sliding-window rate limiter to put a ceiling on those.

