---
applyTo: "Order/**"
description: "Use when working on the Order Service — event-sourced gRPC service with saga orchestration, CQRS read models, transactional outbox, Redis-locked saga resume, compensation with refund retry, and multi-aggregate support (Order, B2B Quote, Recurring Order, Return Request)."
---

# Order Service

## Overview

Event-sourced gRPC service handling the complete order lifecycle across four aggregate types. Uses saga orchestration for multi-step fulfillment, CQRS with separate read models, transactional outbox for Kafka publishing, and Redis distributed locking for concurrent saga protection.

Return shipment delivery callbacks are ingressed via Gateway webhook endpoint and translated into Kafka saga continuation events (`ReturnShipmentDeliveredEvent`) consumed by Order.

## Architecture (Event Sourcing + CQRS + Saga)

- **Api/** — gRPC services (`OrderGrpcService`, `B2BOrderGrpcService`, `RecurringOrderGrpcService`), validators, Program.cs
- **Application/** — Commands, Queries, Sagas (with steps and event handlers), Consumers, DTOs, `ApplicationModule.cs`
- **Domain/** — Aggregate roots (Order, B2BOrder, RequestReturn), Value Objects (OrderStatus, Money, Address, strongly-typed IDs), Domain Events, Services (ReturnPolicyService), Exceptions
- **Infrastructure/** — Event store, Saga repository, Read repositories, Outbox, Kafka messaging, Background services, Redis locking, Region affinity, Serialization, Carrier shipping gateways (`Gateways/Carrier/`: `ShippingGatewayRouter` + `DpdShippingAdapter`/`PplShippingAdapter`, `DpdApiOptions`/`PplApiOptions`), `InfrastructureModule.cs`
- **Protos/** — Separate class library

## Tech Stack

- .NET 8, gRPC
- PostgreSQL + EF Core 8 (event store + read models in separate DbContexts)
- Kafka: Confluent.Kafka (producer + consumer)
- Redis (StackExchange.Redis) for saga distributed locking
- MediatR (command/query dispatch)
- FluentValidation
- OpenTelemetry (Jaeger exporter)
- Testing: NUnit + NSubstitute (unit), xUnit + Testcontainers (integration/E2E)

## Event Sourcing

- **Write**: Aggregate raises domain events → `EventStoreRepository.SaveEventsAsync()` saves to DomainEvents table with version increment
- **Read**: Load events by AggregateId → deserialize by type registry → replay `Apply()` methods on aggregate
- **Snapshots**: Taken every N events (50 for Order, 20 for B2B Quote) → reconstruct from snapshot + events after snapshot
- **Concurrency**: Version-based optimistic concurrency — mismatch throws `ConcurrencyConflictException`
- **Persistence**: `OrderPersistenceService` wraps event save + outbox write in single transaction

## Aggregates & State Machines

### Order (standard)
- Pending → Paid → Approved → Completed | Cancelled
- Methods: `Create()`, `Pay()`, `Approve()`, `Complete()`, `Cancel()`, `AssignTracking()`, `RevertTrackingAssignment()`

### B2BOrder (living quote)
- Draft → Finalized | Cancelled
- Long-lived (days/weeks), low snapshot threshold (20 events due to many draft edits)
- Methods: `Start()`, `AddItem()`, `RemoveItem()`, `ChangeItemQuantity()`, `AdjustItemPrice()`, `ApplyDiscount()`, `AddComment()`, `Finalize()`, `Cancel()`
- Finalization creates a child standard Order

### Recurring Order
- Schedule-based; `RecurringOrderSchedulerService` polls due orders every 60s
- Executes via standard order creation flow

### Return Request
- Pending → Received → Refunded → Completed
- ReturnSaga handles multi-day physical delivery wait

## Saga Orchestration

### SagaBase (Generic Orchestrator)
- `ExecuteAsync()`: Runs steps sequentially, handles WaitForEvent/Fail
- `ResumeFromStepAsync()`: Skips completed steps, re-runs the specified step
- `CompensateAsync()`: Reverses steps in order (last completed → first)
- Saga timeout: 5 min total (per-saga, not per-step)
- States: Running → Completed | WaitingForEvent | Failed | TimedOut | FailedToCompensate

### OrderSaga Steps (authorize-early / capture-late)
0. **CancelOrderOnFailureStep** — no-op on execute; compensation cancels order
1. **ReserveInventoryStep** — calls InventoryGateway.ReserveAsync()
2. **AuthorizePaymentStep** — places an authorization HOLD (money reserved, not moved):
   - Frontend pre-auth: `data.PaymentIntentId` present → record the browser-created hold (3DS already done client-side) → status `Authorized`
   - Backend-initiated (B2B/recurring/COD): `AuthorizeAsync()` (manual capture) → `Authorized`; immediate-settle methods (invoice) treated as authorized; `Pending`/`RequiresAction` (defensive server-side 3DS, unused for B2B) → WaitForEvent
   - DeadlineExceeded → Uncertain + WaitForEvent
   - Compensation: VOID the hold via `CancelAuthorizationAsync()` (free); no-op once captured
3. **AwaitPaymentConfirmationStep** — `Authorized` proceeds; WaitForEvent if Pending/Uncertain
4. **UpdateOrderStatusStep** — confirm inventory, set order to Paid
5. **CreateShipmentStep** — create shipment, assign tracking
6. **CapturePaymentStep** — captures the hold (MONEY MOVES) via `CaptureAsync(providerPaymentIntentId)`:
   - Uses `context.ProviderPaymentIntentId` ?? `data.PaymentIntentId`
   - Compensation: REFUND via `RefundWithStatusAsync()` (only meaningful once captured)
7. **CompleteOrderStep** — approve + complete order
8. **SendConfirmationEmailStep** — send email (3 retries; failure doesn't fail saga)

**Money model**: authorize early (step 2) → capture late (step 6). A failure before capture only voids the hold (instant, free); only post-capture failures refund. `OrderSagaPaymentStatus` includes `Authorized`. The frontend does 3DS in the browser before submitting the order, so the backend authorize is synchronous.

### ReturnSaga Steps (6 steps)
- ValidateReturnRequest → AwaitReturnShipment (waits days) → ConfirmReceived → ProcessRefund → UpdateAccounting → CompleteReturn

### Return webhook callback flow
- `AwaitReturnShipmentStep` registers shipping callback using configurable `Shipping:WebhookCallUrl` (never hardcoded)
- Callback URL includes correlation query params (`orderId`, `shipmentId`) to preserve routing context
- External carrier callback is received by Gateway and published to `order.events` as `ReturnShipmentDeliveredEvent`
- `ReturnShipmentDeliveredEventHandler` resumes `ReturnSaga` at `ConfirmReturnReceived`

### Shipping carrier gateways
- `ShippingGatewayRouter` selects an adapter by `ShippingCarrier` enum (`Dpd`, `Ppl`) and wraps/unwraps external IDs as `{Carrier}:{rawId}` (e.g. `Ppl:ppl-ret-...`) so the saga stays carrier-agnostic
- `DpdShippingAdapter` — synchronous: create returns the shipment + tracking immediately; cancel is idempotent; Bearer auth
- `PplShippingAdapter` — **two-phase**: `CreateShipmentAsync` reads the `202` booking then polls `GET api/v1/parcels/{referenceId}` up to `PplApiOptions.MaxPolls` (delay `PollIntervalMs`): `accepted` → `ShipmentResultDto`, `rejected` → `InvalidAddressException`, exhaustion → `PplBookingPendingException` (NOT `TimeoutException`); Bearer auth. Also implements `IPplBookingPoller.PollAsync` for the reconciliation worker.
- `PplBookingPendingException` carries `ReferenceId`; caught in `CreateShipmentStep` which enqueues a `PplPendingBookings` row and returns `Completed` — saga finishes without tracking. `PplBookingReconciliationWorker` resolves it asynchronously.
- Adapters target deterministic fakes in `partners/my-dpd` (`:8091`) and `partners/my-ppl` (`:8092`); the carrier-tagged webhook each fires is validated and branch-authenticated by the Gateway

### Saga Event Handlers
- **Creation**: `OrderCreatedEventHandler` — starts saga on OrderCreatedEvent from Kafka
- **Continuation**: `PaymentSucceededEventHandler`/`PaymentFailedEventHandler` — resumes paused saga with Redis lock
- **Return continuation**: `ReturnShipmentDeliveredEventHandler` — resumes `ReturnSaga` when return delivery callback arrives via Gateway→Kafka

### Redis Saga Locking
- Key: `saga-lock:OrderSaga:{correlationId}`
- Acquire: SET-NX with 6-min TTL
- Release: Atomic Lua compare-and-delete (ownership check)
- Prevents concurrent resume from duplicate webhooks or multiple replicas

## Compensation & Refund Retry

- Steps compensate in reverse order
- Payment compensation is split across the two money steps (reverse order: capture first, then authorize):
   - **CapturePaymentStep** (captured): `Succeeded` + PaymentId → `RefundWithStatusAsync()`
   - **AuthorizePaymentStep** (hold): `Authorized` → `CancelAuthorizationAsync()` (void, free); `Succeeded` (already captured) → no-op; void failure → critical incident `AuthorizationVoidFailed`
   - Uncertain + PaymentId present: enqueue deferred verification/retry row in `CompensationRefundRetries`
   - Uncertain + no PaymentId but paymentIntentId present: call `CancelAuthorizationAsync()` to release pre-auth hold
   - Uncertain + no identifiers: raise critical incident (manual reconciliation required)
- Retriable refund failures (GatewayUnavailable/timeout) → `CompensationRefundRetries` table
- `CompensationRefundRetryWorker`: exponential backoff (30s base, 900s max), max 3 retries
- Dedupe: unique filtered index on (OrderId, PaymentId) WHERE Status=Pending

## Outbox Pattern

- Events + outbox message + idempotency record saved in single transaction
- `OutboxProcessor`: polls every 2s, groups by AggregateId (causal ordering), parallel across groups (MaxParallelism=5)
- Max retries: 5, then moved to DeadLetterMessage
- Cleanup: processed messages deleted after 7 days

## Read Model Synchronization

- Separate `ReadDbContext` (denormalized tables: OrderReadModel, B2BOrderReadModel, etc.)
- `KafkaReadModelSynchronizer`: consumes order/return events from Kafka
- `ReadModelEventDispatcher` routes to appropriate updater
- Retry: 3 immediate retries with jitter; systemic failure → pause partition; message-specific → KafkaRetryRecord

## Region Affinity

- `DeterministicWriteRegionOwnershipResolver`: hashes CustomerId → deterministic region
- `CreateOrderCommandHandler` rejects non-owner region with owner hint
- Risk acceptance: ~0.1% duplicate orders possible (handled by compensation)

## Idempotency (3 layers)

1. **API boundary**: IdempotencyKey on CreateOrder → IdempotencyRepository
2. **Saga creation**: Check existing SagaState by correlationId
3. **Saga resume**: Redis distributed lock prevents concurrent webhook processing

## Background Services

| Service | Interval | Purpose |
|---------|----------|---------|
| OutboxProcessor | 2s poll | Publish events to Kafka |
| SagaOrchestrationService | Real-time | Consume order.events, dispatch saga handlers |
| SagaWatchdogService | 1 min | Monitor stuck sagas (>5 min Running), force compensation |
| RecurringOrderSchedulerService | 60s poll | Execute due recurring orders |
| CompensationRefundRetryWorker | 30s poll | Retry failed refunds with exponential backoff |
| PplBookingReconciliationWorker | 30 min poll | Retry PPL booking status checks; on accepted assigns tracking; on exhaustion fires Telegram intervention ticket |
| KafkaReadModelSynchronizer | Real-time | Sync read models from events |

## Database Schema (AppDbContext + ReadDbContext)

**Write side**: DomainEvents, AggregateSnapshot, OutboxMessage, DeadLetterMessage, SagaState, SagaStepLog, IdempotencyRecord, CompensationRefundRetry, ProcessedEvent, KafkaRetryRecord, PplPendingBookings
**Read side**: OrderReadModel, B2BOrderReadModel, RecurringOrderReadModel, ReturnRequestReadModel, RequestReturnLookup

## Configuration

- Outbox: BatchSize=20, MaxRetries=5, MaxAgeDays=7, PollIntervalMs=2000, MaxParallelism=5
- RecurringOrder: SchedulerIntervalSeconds=60, BatchSize=50
- CompensationRefundRetry: BatchSize=20, MaxRetries=3, PollIntervalSeconds=30, BaseRetryDelaySeconds=30, MaxRetryDelaySeconds=900
- PplBookingReconciliation: BatchSize=10, MaxAttempts=48 (~24h), PollIntervalSeconds=1800, RetryIntervalSeconds=1800
- Kafka: BootstrapServers, OrderEventsTopic, ReturnEventsTopic, SagaTopic
- WriteRouting: Enabled, CurrentRegion, Regions list
- Shipping: `WebhookCallUrl` for return webhook registration target (Gateway public webhook endpoint)
- Shipping carriers: `Shipping:Dpd` and `Shipping:Ppl` (`BaseUrl`, `ApiKey`, `TimeoutSeconds`); `Ppl` adds `MaxPolls` and `PollIntervalMs` for the two-phase booking poll loop

## Testing

- **Unit**: NUnit + NSubstitute — saga steps, aggregate behavior, command handlers, watchdog transitions
- **Integration**: xUnit + Testcontainers — saga execution + compensation, SagaRepository persistence, read model sync
- **E2E**: Full gRPC + Kafka — happy path + watchdog compensation scenarios

## Key Rules

- **Event sourcing is the source of truth** — never mutate state without raising a domain event
- **Saga timeout is per-saga (5 min total), not per-step** — a slow step starves subsequent ones
- **DeadlineExceeded ≠ failure** — it means Uncertain; wait for webhook/reconciliation to resolve
- **Authorize early, capture late** — `AuthorizePaymentStep` (step 2) holds funds; `CapturePaymentStep` (step 6) moves them just before completion. Pre-capture failures VOID the hold (free); only post-capture failures REFUND. `AuthorizeAsync` calls Payment `ProcessPayment` with `capture_method=manual`
- **Uncertain compensation must never silently no-op** — execute explicit safety path (deferred verification and/or authorization cancel + incident on missing identifiers)
- **WaitingForEvent sagas must not be killed by watchdog** — verify GetStuckSagasAsync excludes them
- **Outbox guarantees causal ordering within an aggregate** — but parallel across aggregates may reorder within a batch
- **Region affinity is advisory** — duplicates are rare but possible; saga idempotency is the real guard
- **B2B Quote snapshots at 20 events** (not 50) because draft editing creates many events
- **Proto uses `double` for money** — precision loss exists at proto boundary (known issue)
- **String matching on concurrency exceptions is fragile** — known tech debt
- **Never hardcode webhook callback URLs in saga steps** — always resolve from config/provider
- **PPL booking is two-phase** — the adapter must poll after the `202`; a `rejected` poll is an `InvalidAddressException`, poll-window exhaustion is a `PplBookingPendingException` (never treat the initial `202` as success, never throw `TimeoutException`)
- **PPL poll exhaustion must not fail the saga** — catch `PplBookingPendingException` in `CreateShipmentStep`, enqueue to `PplPendingBookings`, return `Completed`; `PplBookingReconciliationWorker` resolves it; exhaustion after `MaxAttempts` fires a Telegram intervention ticket
- **PPL cancel can be refused (`409`) once in transit** — `CreateShipmentStep` compensation logs it non-retryable and raises an intervention ticket while the other steps still compensate
- **Carrier callbacks ingress through Gateway, not directly into Order** — Gateway translates webhook payload to Kafka saga events
