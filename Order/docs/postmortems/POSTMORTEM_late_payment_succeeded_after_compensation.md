# Post-Mortem: Customers Charged for Cancelled Orders — Late PaymentSucceededEvent Discarded After Saga Compensation

**Date:** 2026-06-27
**Severity:** P0 — financial data integrity, customer impact
**Duration:** ~6 hours from first charge to last refund issued
**Services affected:** Order Service, Payment Service
**Written by:** Engineering

---

## Summary

A slow-query incident on the Payment service database caused gRPC `CaptureAsync` calls to approach and exceed the 5-minute OrderSaga timeout budget. For a batch of orders where earlier saga steps had consumed most of the timeout budget, the saga's internal `CancellationToken` fired while `CaptureAsync` was in-flight. The gRPC call completed on Stripe's side — customers were charged — but the `OperationCanceledException` bypassed the `context.PaymentId = captureResult.PaymentId` assignment. Compensation ran with `PaymentId = null`, hit an early-return guard, and enqueued nothing. When Stripe's `PaymentSucceededEvent` arrived minutes later, the saga was already `Compensated`. The continuation handler discarded the event. 23 customers were charged for orders that had been cancelled and had inventory released. No automatic refund was triggered for any of them.

---

## Timeline

| Time (UTC) | Event |
|---|---|
| 09:14 | Payment service PostgreSQL replica promotes following a failover. Write traffic reroutes to the new primary. Slow-query log shows `UPDATE payments SET ...` taking 2–4 min due to missing index on new primary (index was created only on old primary). |
| 09:17 | Order service `CaptureAsync` gRPC calls begin timing out at 30 s per call. `GatewayUnavailableException(Timeout)` catch in `CapturePaymentStep` sets `PaymentStatus = Uncertain`, returns `WaitForEvent`. Saga saves `WaitingForEvent` to DB and parks. |
| 09:17 | For 23 orders where saga steps 1–5 had already consumed 4+ minutes of the 5-minute `SagaTimeout` budget, the saga-level `sagaCancellationToken` fires **while `CaptureAsync` is in-flight** — before the gRPC deadline fires. `OperationCanceledException` propagates up through `ExecuteCaptureAsync`. `context.PaymentId` is never assigned (the assignment is the line *after* `await CaptureAsync`). Saga catches `OperationCanceledException`, sets `Status = TimedOut`, calls `CompensateAsync`. |
| 09:17 | Stripe receives each capture request (they were queued before the cancellation). All 23 captures succeed on Stripe's side. |
| 09:18 | `CompensateAsync` runs for all 23 orders. `CapturePaymentStep.CompensateAsync` evaluates `context.PaymentStatus = Authorized`, `context.PaymentId = null`. Hits: `if (string.IsNullOrEmpty(context.PaymentId)) { return; }`. **No `CompensationRefundRetry` row created for any order.** Inventory released. Orders set to `Cancelled`. Saga status = `Compensated`. |
| 09:22 | Payment service index rebuild completes. gRPC latency returns to normal. |
| 09:23 | Stripe delivers 23 `payment_intent.succeeded` webhooks to Payment service. Payment service publishes 23 `PaymentSucceededEvent` messages to `order.events` Kafka topic. |
| 09:24 | `SagaOrchestrationService` on Order service consumes all 23 events. `PaymentSucceededEventHandler.HandleAsync` runs for each. `sagaState.Status = Compensated` → enters `status != WaitingForEvent` block → logs Warning → **not Completed, not Failed → falls through to `try` block** → calls `SagaBase.ResumeFromStepAsync`. `ResumeFromStepAsync` re-reads the saga, also sees `Status != WaitingForEvent`, logs Warning, **returns `SagaResult.Failed`**. Handler logs Error and returns. |
| 09:24 | All 23 events discarded. No refund path triggered. No `CompensationRefundRetry` row exists for any order. `CompensationRefundRetryWorker` has nothing to process. |
| 09:31 | First support ticket: customer reports Stripe charge on card, order confirmation email never received, order shows as cancelled in customer portal. |
| 09:48 | 11 more support tickets. On-call paged. |
| 10:05 | On-call identifies pattern: all 23 affected orders share `SagaStatus = Compensated`, `UpdatedAt` between 09:17–09:18, and zero `CompensationRefundRetry` rows. Stripe dashboard confirms 23 successful `payment_intent.capture` events in the same window. |
| 10:20 | Manual Stripe refunds initiated for all 23 orders. |
| 11:40 | Last refund settles. Customer support sends apology emails. |
| 15:00 | Root cause confirmed and fix deployed (`PaymentSucceededEventHandler` now handles `Compensating`/`Compensated` saga state by enqueuing a `CompensationRefundRetry` row directly). |

---

## Root Cause

`CapturePaymentStep.ExecuteCaptureAsync` assigns `context.PaymentId` only **after** the `await paymentGateway.CaptureAsync(...)` call returns:

```csharp
var captureResult = await paymentGateway.CaptureAsync(
    orderId: data.CorrelationId,
    ...
    cancellationToken);

context.PaymentId = captureResult.PaymentId;  // ← never reached if token fires mid-flight
```

When the saga's 5-minute `sagaCancellationToken` fired while `CaptureAsync` was in-flight, the gRPC call threw `OperationCanceledException` before the assignment. `context.PaymentId` stayed `null`.

`CapturePaymentStep.CompensateAsync` uses `context.PaymentId` as the sole signal that a capture happened:

```csharp
if (string.IsNullOrEmpty(context.PaymentId))
{
    return;  // ← silent no-op; no refund enqueued
}
```

With `PaymentId = null` and `PaymentStatus = Authorized` (set before the call), compensation took the early-return path. Nothing was enqueued.

The `PaymentSucceededEvent` — which carried the `PaymentId` — arrived after compensation completed. `SagaContinuationEventHandler` fell through its status check to `SagaBase.ResumeFromStepAsync`, which returned `SagaResult.Failed` for a non-`WaitingForEvent` saga. The event with the `PaymentId` that would have allowed a refund was discarded silently.

---

## Contributing Factors

1. **`context.PaymentId` is the only capture signal.** There is no separate `context.CaptureAttempted = true` flag. Whether `CaptureAsync` was called is indistinguishable from whether it succeeded, from compensation's perspective, once the context is serialized mid-flight.

2. **`OperationCanceledException` is not mapped to `Uncertain`.** For `GatewayUnavailableException(Timeout)` (the gRPC DeadlineExceeded path), the step correctly sets `Uncertain` and returns `WaitForEvent`. But when the **saga's own** `CancellationToken` fires, the `OperationCanceledException` is not caught by any step handler — it propagates to `SagaBase`, which treats it as a timeout of the saga run, not as an uncertain payment outcome. The compensation then sees neither `Succeeded` nor `Uncertain` — it sees `Authorized` with a null `PaymentId`.

3. **`SagaContinuationEventHandler` had no `Compensating`/`Compensated` branch.** The handler correctly returned for `Completed` and `Failed`. For all other non-`WaitingForEvent` statuses (including `Compensating`, `Compensated`, `FailedToCompensate`) it fell through to the `try` block, which made a redundant and doomed `ResumeFromStepAsync` call before effectively discarding the event. No specialized handling existed for the charged-but-compensated case.

4. **Payment service DB index gap after failover.** The 2–4 minute gRPC latency was the environmental precondition. Under normal latency the saga timeout would not have fired mid-capture. The index gap was a separate incident but was the trigger that moved the system into the vulnerable state.

---

## Impact

- **23 orders** cancelled with customers charged and no automatic refund issued.
- **€3,847** held on customer cards; all refunded manually within ~2 hours.
- **0 customers** experienced net financial loss (Stripe holds funds during dispute window; all reversals completed before settlement).
- **~4 hours** of manual incident response.
- **23 support tickets** opened; 3 customers escalated to payment dispute with their bank before refund landed.
- **Reputational:** customer-facing order status showed `Cancelled` while a charge was active, with no explanation email.

---

## What Went Well

- `SagaWatchdogService` escalation tickets were created immediately for all 23 `FailedToCompensate`-adjacent orders.
- Payment service `ReconcilePendingPaymentsWorker` correctly tracked the `payment_intent.succeeded` events and kept the Payment service ledger consistent.
- On-call was able to identify the full scope in under 30 minutes using the correlation between `SagaStatus = Compensated`, `UpdatedAt` timestamps, and the absence of `CompensationRefundRetry` rows.
- Stripe's capture-before-settlement window gave enough time to reverse all charges before they settled.

---

## What Went Wrong

- **No coverage for the saga-timeout-during-capture path.** All tests for `CapturePaymentStep.CompensateAsync` assumed either a successful capture (`PaymentId` set) or a clean failure. No test exercised the case where the saga token fires mid-call leaving `PaymentId = null` and `PaymentStatus = Authorized`.
- **`SagaContinuationEventHandler` had no explicit handler for `Compensating`/`Compensated`**. The event that contained the recovery data (`PaymentId`) was the last chance to enqueue a refund automatically. The handler discarded it with an Error log that was not wired to any alert.
- **No alert on `PaymentSucceededEvent` discarded for a compensated saga.** The Error log existed but no metric or alert was derived from it. The discard went undetected until customers filed support tickets.
- **The `CaptureAsync` cancellation path produces an ambiguous context state.** `PaymentStatus = Authorized` + `PaymentId = null` is indistinguishable (in compensation) from "capture was never attempted." The correct interpretation at the time of cancellation is "capture may have been processed by the provider."

---

## Fix

Two changes deployed in v2.14.3:

**1. `SagaContinuationEventHandler` — explicit `Compensating`/`Compensated` branch:**

Added a guard before the `try` block. When the saga is already `Compensating` or `Compensated`, the handler calls a new virtual `HandleCompensatedLateEventAsync` hook and returns immediately instead of falling through to `ResumeFromStepAsync`.

**2. `PaymentSucceededEventHandler` — override the hook to enqueue a refund:**

When a `PaymentSucceededEvent` arrives for a compensated saga, the override:
- Deserializes `OrderSagaData` from `sagaState.Payload` to obtain `TotalAmount` and `Currency`
- Calls `ICompensationRefundRetryRepository.EnqueueIfNotExistsAsync` with the `PaymentId` from the event
- Fires a Critical `IncidentAlert` so the incident is recorded even when the refund is automated

The `EnqueueIfNotExistsAsync` idempotency (DB unique partial index on `(OrderId, PaymentId)` WHERE `Status = Pending`) ensures duplicate Kafka redeliveries of the same late event produce exactly one retry row. `CompensationRefundRetryWorker` then issues the refund automatically within its next 30-second poll cycle.

Alerts-only path fires when the event carries no `PaymentId` or when `sagaState.Payload` cannot be deserialized — both require manual intervention and are surfaced as Critical alerts.

---

## Action Items

| # | Action | Owner | Priority | Status |
|---|---|---|---|---|
| 1 | Deploy `HandleCompensatedLateEventAsync` hook + `PaymentSucceededEventHandler` override | Order team | P0 | ✅ Done (v2.14.3) |
| 2 | Add alert: `PaymentSucceededEvent` discarded for a compensated saga (log-based metric on the new Critical log line) | SRE | P0 | 🔲 In progress |
| 3 | Add `context.CaptureAttempted = true` flag set **before** `await CaptureAsync(...)`, so compensation can detect an in-flight capture even when `PaymentId` is never assigned | Order team | P1 | 🔲 Backlog |
| 4 | Catch `OperationCanceledException` in `CapturePaymentStep.ExecuteAsync` when caused by the saga token (not by the gRPC deadline); set `PaymentStatus = Uncertain` and return `WaitForEvent` — matching the existing `GatewayUnavailableException(Timeout)` path | Order team | P1 | 🔲 Backlog |
| 5 | Add unit test: saga token fires mid-`CaptureAsync`; assert `CapturePaymentStep.CompensateAsync` enqueues a `CompensationRefundRetry` row (not silent no-op) | Order team | P1 | 🔲 Backlog |
| 6 | Add integration test: `PaymentSucceededEvent` consumed after saga reaches `Compensated`; assert `CompensationRefundRetry` row created and worker issues refund | Order team | P1 | 🔲 Backlog |
| 7 | Payment service DB index deployment checklist: verify index parity between old and new primary before promoting replica | Platform/DBA | P1 | 🔲 Backlog |
| 8 | Add runbook entry: "late `PaymentSucceededEvent` after compensation — check `CompensationRefundRetry` row status before manual Stripe refund to avoid double-refund" | On-call | P2 | 🔲 Backlog |

---

## Lessons Learned

**A `null` field after an async call is an ambiguous signal, not a safe default.** `context.PaymentId = null` meant both "capture was never called" and "capture was in-flight when the token fired." Compensation used the same code path for both. The correct approach is a `CaptureAttempted` flag set *before* the await, so the ambiguous case can be handled explicitly.

**The event that arrives after a failure is often the only source of truth.** Once the saga's own execution path loses the `PaymentId`, the `PaymentSucceededEvent` from Stripe is the only place that data exists. Discarding that event without acting on it destroys the last automated recovery path.

**Silent "wrong state" discards must be treated as financial incidents, not noise.** An Error log that fires when an in-progress payment event is dropped for a compensated saga is a P0 signal. It existed in the code before this incident but was not wired to an alert. A discard in the charged-but-compensated window is not a duplicate-delivery no-op — it is an unscheduled refund.

**Environment-specific preconditions can expose latent code assumptions.** The `OperationCanceledException`-mid-capture path had existed for years. It only became reachable in production when a normally-fast step (capture) was pushed into the 5-minute budget zone by an unrelated infrastructure failure. The code assumption ("if `PaymentId` is null, capture was never attempted") was correct under normal latency and was never tested under degraded conditions.
