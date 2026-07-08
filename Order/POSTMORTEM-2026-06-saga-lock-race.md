# Post-Mortem: Saga Lock Race — Double Compensation & Refund/Re-charge Interleave
**Date:** 2026-06-15  
**Severity:** P0 — Money  
**Duration:** ~4 hours (first alert to full mitigation)  
**Author:** on-call SRE + Order service team  
**Status:** Resolved (root cause fixed 2026-06-27)

---

## Summary

Under sustained load, a subset of orders that hit the saga timeout path were simultaneously compensated **and** re-charged. Customers received a refund notification followed immediately by a second payment charge. The inventory for ~12 orders was released and then re-reserved in an overlapping window, causing stock to briefly go negative for two SKUs. Financial ledger diverged from Stripe by €847 across 9 transactions.

---

## Timeline

| Time (UTC) | Event |
|---|---|
| 14:02 | Deployment of Order service v2.14.1 to production (unrelated change — PPL adapter timeout tuning). Increased PPL `MaxPolls` from 6 → 18, effectively tripling the worst-case `CreateShipmentStep` duration. |
| 14:09 | First alert: `saga_compensation_started_total` counter spikes. Saga watchdog detects a batch of orders stuck in `TimedOut`. Normal — carrier latency elevated. |
| 14:23 | First anomaly: Stripe webhook `payment_intent.succeeded` fires for order `ord-8a3f...` which is already in `Compensating`. Payment service queues a `payment.succeeded` callback. |
| 14:24 | `SagaContinuationEventHandler` for `PaymentSucceededEvent` **acquires the saga lock** for `ord-8a3f...` — lock TTL = 6 min. |
| 14:24 | Concurrently, `CompensateAsync` for `ord-8a3f...` is running under the service cancellation token with **no timeout**. It is mid-way through `CancelShipmentStep`, retrying against a slow PPL endpoint (attempt 2/3, backoff = 4 s). |
| 14:24 | `SagaContinuationEventHandler` reads saga status = `Compensating`, logs "Saga not in waiting state", returns early. **No double-resume this time** — saga status guard catches it. |
| 14:31 | Lock for `ord-8a3f...` was acquired at 14:24 by the late webhook handler, held for 6 min (TTL), and auto-expired by Redis at **14:30**. Compensation for `ord-8a3f...` is still running (PPL carrier slow). |
| 14:31 | Kafka consumer delivers a **duplicate** `PaymentSucceededEvent` for `ord-8a3f...` (Kafka at-least-once redelivery after consumer group rebalance during deploy). Lock is gone. Lock acquired. |
| 14:31 | Duplicate event handler reads saga status = `Compensating`. Returns early. Safe again — but we got lucky. |
| 14:38 | For order `ord-c91b...`: saga times out at 14:33. Compensation starts. At 14:39 (6 min after the continuation event handler acquired the lock at 14:33) **the lock expires**. |
| 14:39 | SagaWatchdog (30-second poll) picks up `ord-c91b...` as `TimedOut`, calls `CompensateAsync` directly. Acquires no lock (watchdog does not use the distributed lock). Begins compensation. |
| 14:39 | **Two concurrent compensation runs for `ord-c91b...`**: original timeout path (still alive, running `ReleaseReservationStep`) and watchdog path (starting from the beginning, running `CancelPaymentStep`). |
| 14:39 | Original path: `ReleaseReservationAsync` → Inventory returns `OK`, reservation released. Stock +2. |
| 14:39 | Watchdog path: `CancelPaymentStep` calls Payment service → `MarkRefundFailed` (payment already in `RefundPending` from original path). Payment state machine throws `InvalidPaymentStateTransitionException`. Watchdog compensation catches it as non-transient, saga lands in `FailedToCompensate`. Escalation ticket #4471 created. |
| 14:40 | Original path continues to `CancelPaymentStep` — refund **succeeds**. Order saga status = `Compensated`. |
| 14:40 | But watchdog had already written `FailedToCompensate` to DB (line 14:39). Original path overwrites it back to `Compensated` (no optimistic concurrency on saga state). |
| 14:41 | For order `ord-f02d...`: identical scenario but watchdog path wins the `CancelPaymentStep` race. Both paths attempt `ReleaseReservationAsync`. First call succeeds (reservation `Active` → released). Second call: Inventory returns `404 reservation not found`. Inventory gateway throws. **Stock released twice in-memory before the 404, but only committed once** — net effect neutral. |
| 14:47 | For order `ord-2e9c...`: watchdog path calls `CapturePaymentStep.CompensateAsync` (cancel authorize) AND original timeout path reaches the same step 200ms later. Both cancel-authorize requests reach Stripe. First: `200 OK`. Second: `200 OK` (idempotent on Stripe's side). Both call `QueuePaymentSucceededAsync` → **two callbacks queued** (random `Guid.NewGuid()` IDs, no unique constraint on `(OrderId, EventType)`). Order saga receives `payment.succeeded` twice, resumes forward from `AwaitPaymentConfirmation` **twice**. Customer charged again. |
| 14:51 | On-call engineer pages payment team. Manual Stripe refund issued for `ord-2e9c...`. |
| 15:10 | Root cause identified: lock TTL (6 min) < saga timeout (5 min) + compensation duration (unbounded). |
| 15:15 | Mitigation: restarted Order service with `PPL_MAX_POLLS=6` (reverted v2.14.1 change). Compensation duration drops back below 1 min. Lock no longer expires mid-compensation in practice. |
| 18:30 | Fix implemented: `CompensationTimeout = 3 min`, `LockBudget = SagaTimeout + CompensationTimeout`, `_lockExpiry = LockBudget + 1 min = 9 min`. Compensation bounded internally. Deployed v2.14.2. |

---

## Root Cause

**Primary:** Saga distributed lock TTL (6 min) was less than the maximum possible critical-section duration (saga timeout 5 min + unbounded compensation). When PPL carrier latency increased (due to the v2.14.1 `MaxPolls` change), compensation routinely exceeded 1 minute, breaching the lock TTL and opening a concurrent-writer window.

**Contributing — no compensation timeout:** `CompensateAsync` ran on the service cancellation token with no time bound. There was no mechanism to guarantee compensation would finish before the lock expired.

**Contributing — watchdog ignores the distributed lock:** `SagaWatchdogService` calls `CompensateAsync` directly without acquiring the saga lock. This is correct for the watchdog's *detection* role, but it means any compensation triggered by the watchdog can race with an in-progress compensation from the main path if the lock has expired.

**Contributing — non-idempotent saga state writes:** Saga state has no optimistic concurrency token (`xmin`). Two concurrent writers can overwrite each other's status. In the `ord-c91b...` case the `FailedToCompensate` status was overwritten by the correct `Compensated` status — a lucky outcome. The reverse could easily have happened.

---

## Impact

| Metric | Value |
|---|---|
| Orders affected | 12 |
| Orders with customer-visible double-charge | 1 (`ord-2e9c...`) |
| Manual Stripe refunds issued | 1 (€94.99) |
| Ledger/Stripe divergence | €847 across 9 transactions (all reconciled within 24h by `ReconcilePendingPaymentsWorker`) |
| Inventory count errors | 2 SKUs briefly negative, self-corrected within 1 min |
| Escalation tickets auto-created | 3 |
| Duration of customer impact | ~12 min (14:39–14:51) |

---

## What Went Well

- `SagaWatchdogService` escalation tickets were created immediately, on-call was paged within 4 minutes of the first anomaly.
- Stripe idempotency prevented the double cancel-authorize from becoming a double capture in most cases.
- Payment reconciliation worker self-healed the ledger divergence overnight with no manual intervention beyond the one manual refund.
- `CancelShipmentStep` is idempotent at the carrier level (PPL returns 200 for already-cancelled shipments), so no duplicate carrier cancellations leaked to customers.

---

## What Went Wrong

- The `MaxPolls` change in v2.14.1 was not evaluated against the lock TTL math. There was no documented invariant stating "compensation must complete within N minutes."
- The lock TTL was a magic number (`TimeSpan.FromMinutes(6)`) with a comment saying it "must be greater than SagaTimeout" — the comment was correct but the number was only 1 minute greater, with zero budget for compensation.
- The watchdog does not participate in the distributed lock protocol, creating a second concurrent-writer path that bypasses the single-writer guarantee entirely once a lock expires.
- No alerting existed for concurrent saga state writes or saga status regressions (e.g., `Compensated` → `FailedToCompensate` → `Compensated`).

---

## Action Items

| # | Action | Owner | Priority | Status |
|---|---|---|---|---|
| 1 | Add `CompensationTimeout` (3 min) to `SagaBase`; bound `CompensateAsync` internally; derive `_lockExpiry` from `LockBudget + 1 min` (9 min) | Order team | P0 | ✅ Done (v2.14.2) |
| 2 | Fix step-fail `CompensateAsync` call sites to use `serviceCancellationToken` instead of the timeout-linked token | Order team | P0 | ✅ Done (v2.14.2) |
| 3 | Add optimistic concurrency (`xmin`) to `saga_states` table to prevent concurrent overwrites | Order team | P1 | 🔲 Backlog |
| 4 | Make `SagaWatchdogService` acquire the distributed lock before calling `CompensateAsync`, or skip compensation entirely if the saga is already `Compensating` | Order team | P1 | 🔲 Backlog |
| 5 | Add alert: saga status regression (any terminal status replaced by a different terminal status) | SRE | P1 | 🔲 Backlog |
| 6 | Add alert: `compensation_duration_seconds` p99 > `CompensationTimeout * 0.8` (early warning before TTL breach) | SRE | P2 | 🔲 Backlog |
| 7 | Document the lock TTL invariant (`LockTTL > SagaTimeout + CompensationTimeout`) in the saga architecture doc and add a startup assertion that fails fast if the invariant is violated | Order team | P2 | 🔲 Backlog |
| 8 | Add a change-review checklist item: "does this change affect saga step duration? re-evaluate `CompensationTimeout`." | Engineering process | P2 | 🔲 Backlog |

---

## Lessons Learned

**Magic timeout numbers are load-bearing infrastructure.** The 6-minute lock TTL looked harmless until a single config change (`MaxPolls`) shifted carrier latency into the danger zone. Timeouts that must satisfy mathematical invariants should be computed, not hardcoded, so violations are impossible by construction.

**"Mostly safe" is not safe for money paths.** The distributed lock protected the happy path. It did not protect the timeout+compensation path, which is exactly the path most likely to be stressed under the conditions that cause timeouts. The failure mode was obscure enough to survive code review but common enough to trigger within 37 minutes of an innocuous tuning change.

**Two concurrent compensation paths is a design gap, not an edge case.** The watchdog and the main timeout handler are both legitimate compensation triggers. They need to coordinate via the same lock, not compete on top of it.
