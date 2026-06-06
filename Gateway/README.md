# Gateway

The single entry point for all external HTTP traffic. Translates REST requests into gRPC calls against the backend services, handles JWT auth, and publishes user behavioural events to Kafka.

## Shipping webhooks

Gateway now also accepts shipping-provider callbacks for return deliveries:

- `POST /api/v1/webhooks/shipping/returns/delivered`

This endpoint is designed as external ingress for carrier callbacks. It validates
required fields and then publishes `ReturnShipmentDeliveredEvent` to Kafka saga topic
(`order.events`) so the Order service can resume ReturnSaga asynchronously.

Optional header-based protection:

- `X-Shipping-Webhook-Secret` compared against `WebhookSecurity:ShippingSharedSecret`

This keeps Order service internal and avoids direct internet exposure of saga continuation endpoints.

## Rate limiting

Some endpoints will burn you out fast if left unbounded — credential stuffing on `/login`, bots hammering `/password-reset/request`, scrapers blasting `/search`. The gateway uses ASP.NET Core's built-in sliding-window rate limiter to put a ceiling on those.

