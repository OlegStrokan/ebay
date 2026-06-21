---
applyTo: "Payment/**"
description: "Use when working on the Payment Service — gRPC payment processor with Stripe integration (real + fake), webhook signature verification, outbound callback delivery with HMAC signing, reconciliation worker for stale payments, and strong idempotency via unique constraints."
---

# Payment Service

## Overview

gRPC payment processor that integrates with Stripe for payment capture, refunds, and webhook handling. Implements dual-path finalization (push via Stripe webhook + pull via reconciliation worker), outbound callbacks to notify Order service of payment outcomes, and multi-layer idempotency.

## Architecture (Clean Architecture + CQRS)

- **Api/** — gRPC service (`PaymentGrpcService`), Stripe webhook HTTP endpoint, Admin callback enqueue endpoint, Program.cs
- **Application/** — MediatR command/query handlers (ProcessPayment, CapturePayment, RefundPayment, HandleStripeWebhook, ReconcilePendingPayments, EnqueueOrderCallback), DTOs, Validators, Gateway interfaces
- **Domain/** — Entities (Payment, Refund, OutboundOrderCallback, PaymentWebhookEvent), Value Objects, Enums (PaymentStatus, RefundStatus), Domain Events, Repository interfaces
- **Infrastructure/** — EF Core persistence, Stripe provider (real + fake), Callback dispatcher (HTTP + HMAC), Background workers, Unit of Work
- **Protos/** — Separate class library with `payment.proto` and `common.proto`

## Tech Stack

- .NET 8, gRPC + Minimal API (webhook/admin endpoints)
- PostgreSQL + EF Core 8
- Stripe SDK (Stripe.net v50.4.1) — real + fake mode
- MediatR (command/query dispatch)
- FluentValidation
- Kafka producer (callback delivery option)
- OpenTelemetry
- Testing: NUnit + NSubstitute (unit), xUnit + Testcontainers (integration), WebApplicationFactory (E2E)

## Domain Entities & State Machines

### Payment
- Created → PendingProviderConfirmation → Succeeded | Failed
- Created → **Authorized** → Succeeded (capture) | Failed  *(manual-capture authorize-only flow)*
- Succeeded → RefundPending → Refunded | RefundFailed
- `Authorized` = funds held, not moved; settled later by CapturePayment (move) or CancelAuthorization (void). Emits no webhook and is not reconciled as pending.
- Tracks: PaymentId, OrderId, CustomerId, Amount, Currency, Status, ProviderPaymentIntentId, ClientSecret, FailureReason, ProcessIdempotencyKey

### Refund
- Requested → PendingProviderConfirmation → Succeeded | Failed
- Child of Payment (validates refund amount ≤ available)
- Tracks: RefundId, PaymentId, Amount, Reason, IdempotencyKey, ProviderRefundId

### OutboundOrderCallback
- Pending → Delivered | PermanentFailure (with Failed state for retry cycles)
- Tracks: CallbackEventId, OrderId, PaymentId, EventType, Payload, AttemptCount, NextRetryAt, LastError

### PaymentWebhookEvent
- Received → Processed | Failed | IgnoredDuplicate
- Deduplicates via ProviderEventId unique index

## Payment Processing Flow

### Synchronous path (gRPC ProcessPayment)
```
Order saga → ProcessPayment RPC (capture_method: automatic | manual)
  → Check idempotency (OrderId, ProcessIdempotencyKey)
  → If exists: return cached response
  → Else: Create Payment entity → Call provider (capture_method forwarded)
    → Succeeded: Payment.MarkSucceeded(), queue PaymentSucceededEvent callback
    → RequiresCapture (manual authorize-only): Payment.MarkAuthorized() → return Authorized (no webhook; settled later by CapturePayment)
    → Pending/RequiresAction: Payment.MarkPendingProviderConfirmation(), return status (webhook will finalize)
    → Failed: Payment.MarkFailed(), queue PaymentFailedEvent callback
```

### Asynchronous push path (Stripe Webhook)
```
Stripe → POST /api/v1/webhooks/stripe
  → Verify signature (HMAC-SHA256 with tolerance)
  → Check ProviderEventId dedup
  → Resolve Payment by PaymentId/ProviderPaymentIntentId/ProviderRefundId
  → Apply outcome: Succeed/Fail payment or refund
  → Queue outbound callback (PaymentSucceeded/Failed)
  → Single transaction commit
```

### Asynchronous pull path (Reconciliation)
```
PendingPaymentsReconciliationWorker (every 60s)
  → Query Payments with Status=Pending, UpdatedAt ≤ 15 min ago
  → Poll Stripe: GetPaymentStatusAsync(ProviderPaymentIntentId)
  → Apply outcome → queue callback
  → Same for Refunds with Status=Pending
```

## Payment Provider (selected by `Stripe:ProviderType`)

The `IStripePaymentProvider` abstraction has three implementations chosen at startup via `Stripe:ProviderType` = `Stripe` | `MockFintech` | `Fake`.

### Stripe (`ProviderType=Stripe`)
- Uses Stripe SDK: PaymentIntent.Create/Get (with `CaptureMethod=manual|automatic`), Refund.Create/Get, Capture, Cancel
- Webhook signature verification with `Stripe:WebhookSecret`

### MockFintech (`ProviderType=MockFintech`)
- Thin typed `HttpClient` to the standalone **fintech-sandbox** (`/partners/fintech-sandbox`), a Go service that imitates a Stripe-level processor without moving real money
- REST + `Authorization: Bearer <MockFintech:ApiKey>`; sends `capture_method` ("manual"/"automatic")
- Async outcomes finalized by the sandbox's Stripe-shaped signed webhooks to `/api/v1/webhooks/stripe`; reconciliation polls the sandbox `GET` endpoints
- Sandbox models full lifecycle: authorize (requires_capture) → capture → cancel, plus 8-day auth-hold expiry
- Config: `MockFintech:BaseUrl`, `MockFintech:ApiKey`, `MockFintech:TimeoutSeconds`

### Fake (`ProviderType=Fake`)
- In-memory simulation; deterministic via idempotency-key tokens:
  - "fail" → Failed, "pending" → Pending, "action"/"3ds" → RequiresAction, otherwise → Succeeded
  - manual capture → RequiresCapture (Authorized hold)
- Skips webhook signature verification; used for local dev and tests

## Callback Delivery (Outbound to Order Service)

### OrderCallbackDeliveryWorker (every 5s)
- Queries OutboundOrderCallbacks with Status=Pending/Failed, NextRetryAt ≤ now
- Batch size: 100 per cycle
- Dispatch via HTTP POST with HMAC-SHA256 signature header
- Success → MarkDelivered()
- Failure → increment attempt, exponential backoff (5s × 2^attempt, max 300s)
- After 8 attempts → MarkPermanentFailure()

### HMAC Callback Signing
- Header: `X-Payment-Signature: t={timestamp},v1={hex_signature}`
- SignedPayload = `{timestamp}.{json_payload}`
- HMAC-SHA256 with `OrderCallback:SharedSecret`
- Order service verifies using same shared secret

### Callback Event Types
- PaymentSucceededEvent, PaymentFailedEvent, RefundSucceededEvent, RefundFailedEvent
- Published to Kafka (saga topic) with OrderId as key + event-type header

## Idempotency

- **Payments**: (OrderId, ProcessIdempotencyKey) UNIQUE — duplicate request returns cached result
- **Refunds**: (PaymentId, IdempotencyKey) UNIQUE
- **Webhooks**: (ProviderEventId) UNIQUE — prevents duplicate webhook processing
- **Callbacks**: (CallbackEventId) UNIQUE — prevents duplicate callback queueing
- On unique violation: catch DbUpdateException, re-fetch and return existing record

## gRPC API

- `ProcessPayment(capture_method)` → PaymentId, Status (Succeeded/Pending/Failed/RequiresAction/RequiresCapture), ProviderPaymentIntentId, ClientSecret, ErrorCode; `capture_method="manual"` authorizes only (returns RequiresCapture → Authorized hold)
- `CapturePayment()` → capture a pre-authorized/held intent (moves the held funds)
- `RefundPayment()` → RefundId, Status (Succeeded/Pending/Failed), ProviderRefundId, ErrorCode
- `CancelAuthorization()` → cancel pre-auth (used by Order compensation when payment outcome is Uncertain and capture did not complete)
- `GetPayment()`, `GetPaymentByOrderAndIdempotency()` → query

## Admin Endpoint

- `POST /api/v1/internal/admin/order-callbacks/enqueue` — manually enqueue callbacks for recovery
- Auth: X-Admin-Key header

## Configuration

- **Stripe**: ProviderType (Stripe|MockFintech|Fake), SecretKey, WebhookSecret, WebhookToleranceSeconds (300), DefaultCurrency
- **MockFintech**: BaseUrl, ApiKey, TimeoutSeconds (used when ProviderType=MockFintech)
- **OrderCallback**: EndpointUrl, SharedSecret, TimeoutSeconds, BatchSize (100), MaxAttempts (8), BaseRetryDelaySeconds (5), MaxRetryDelaySeconds (300)
- **Reconciliation**: Enabled, IntervalSeconds (60), OlderThanMinutes (15), BatchSize

## Testing

- **Unit**: NUnit + NSubstitute — all command handlers, validators, domain state transitions, background workers (reflection for private batch methods)
- **Integration**: xUnit + Testcontainers PostgreSQL — MediatR flows (process/refund/webhook/reconcile), repository tests, EfUnitOfWork unique constraint translation
- **E2E**: WebApplicationFactory + Testcontainers — gRPC process/get/refund, webhook endpoint, admin callback enqueue
- InternalsVisibleTo for Application.Tests; NullLogger for internal generic logger compatibility

## Key Rules

- **Dual-path finalization is by design** — webhook (push) and reconciliation (pull) BOTH resolve pending payments; they converge on the same callback mechanism
- **Manual capture = authorize-only** — `ProcessPayment(capture_method=manual)` → RequiresCapture → `Payment.MarkAuthorized()`; emits NO webhook and is NOT reconciled as pending; settled later by CapturePayment (move) or CancelAuthorization (void)
- **Never skip idempotency checks** — unique constraints are the last line of defense
- **State machine transitions are validated** — invalid transitions throw; never set status directly
- **Refund amount validated against available** — cannot refund more than captured amount
- **Fake provider behavior is deterministic** — tokens in idempotency key control outcome (for testing)
- **Callback delivery is at-least-once** — Order service must handle duplicate callbacks idempotently
- **HMAC signing format must match Order service verification** — `t={timestamp},v1={signature}`
- **Reconciliation worker is the safety net** — catches cases where webhook was lost or delayed
- **Refund `Pending` is an accepted async outcome** — upstream Order may persist deferred verification and retry later via its compensation worker
- **Set `Stripe:ProviderType=Fake` in E2E tests** — override via `ConfigureAppConfiguration` with in-memory settings, not `services.Configure` lambda mutation
