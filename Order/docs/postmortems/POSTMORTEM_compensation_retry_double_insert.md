# Post-Mortem: Double Compensation Execution Due to Missing Partial Unique Index

**Date:** 2026-06-27  
**Severity:** P0 — financial data integrity  
**Duration:** ~4 hours from first symptom to full mitigation  
**Services affected:** Order Service, Payment Service  
**Written by:** Engineering

---

## Summary

A missing partial unique index on the `FailedCompensationRetries` table allowed two concurrent compensation attempts for the same saga to each insert their own retry row. The `CompensationRetryWorker` processed both rows in parallel, calling `CompensateAsync` twice for the same saga. This resulted in duplicate refund calls to the Payment Service for a subset of orders where compensation had previously failed. Stripe idempotency keys prevented most duplicate charges from settling, but 11 refunds were issued twice and required manual reversal.

---

## Timeline

| Time (UTC) | Event |
|---|---|
| 03:12 | Payment Service experiences a 6-minute partial outage (database connection pool exhaustion). |
| 03:13 | Order Service saga compensation starts failing for orders whose `CapturePaymentStep` was in the refund path. `FailedToCompensate` status set for 47 sagas. |
| 03:13 | `CompensationRetryWorker` (primary replica) and `SagaWatchdogService` (secondary replica — Kubernetes had two pods running) both detect the failed compensations within the same poll window. |
| 03:14 | Both paths call `EnqueueIfNotExistsAsync` concurrently for the same saga IDs. Application-level "check then insert" reads see zero rows on both replicas simultaneously. Both inserts succeed — no constraint blocks them. 47 sagas each end up with 2 `Pending` rows. |
| 03:18 | Payment Service recovers. `CompensationRetryWorker` on both replicas claims rows via `SELECT FOR UPDATE SKIP LOCKED`. SKIP LOCKED distributes the 94 rows evenly between the two workers — each worker gets ~47 rows, but rows for the same saga are split across workers. |
| 03:19 | Both workers call `CompensateAsync` for overlapping saga IDs. `CompensateAsync` itself has no distributed lock — it was assumed the retry table deduplication would prevent concurrency. |
| 03:19–03:22 | For 11 sagas, both workers reach `CapturePaymentStep.CompensateAsync` and call `PaymentGateway.RefundWithStatusAsync` with different idempotency keys (one per `CompensationRefundRetry` row, generated from `OrderId + RetryRowId`). Stripe treats them as separate refunds. Both succeed. |
| 03:24 | On-call receives Stripe webhook alerts for anomalous refund volume. |
| 03:31 | On-call identifies duplicate rows in `FailedCompensationRetries` and stops `CompensationRetryWorker` on both replicas by scaling deployment to 0. |
| 03:45 | Duplicate refunds confirmed: 11 orders refunded twice, total over-refund $4,180. |
| 04:10 | Manual reversal of 11 duplicate refunds initiated via Stripe dashboard. |
| 07:20 | All reversals settled. Partial unique index deployed. `CompensationRetryWorker` restarted. |

---

## Root Cause

`FailedCompensationRetryRepository.EnqueueIfNotExistsAsync` used an application-level read-before-write guard:

```csharp
var existing = await dbContext.FailedCompensationRetries
    .FirstOrDefaultAsync(x => x.SagaId == sagaId && (x.Status == Pending || x.Status == InProgress));

if (existing is not null) return; // already queued

await dbContext.FailedCompensationRetries.AddAsync(retry);
await dbContext.SaveChangesAsync();
```

Under concurrent execution the `FirstOrDefaultAsync` on both callers returns `null` before either has committed. Both proceed to insert. PostgreSQL has no constraint to reject the second insert — the table had no unique index on `SagaId`.

The `SELECT FOR UPDATE SKIP LOCKED` in `ClaimDuePendingAsync` correctly prevents two workers from claiming the **same row**, but it cannot prevent two workers from each claiming a **different row for the same saga** — which is exactly what happened once there were two rows.

---

## Contributing Factors

1. **Two pods running during rollout.** A rolling restart had left both old and new replicas alive for ~8 minutes. Both ran `CompensationRetryWorker`.

2. **`SagaWatchdogService` also calls `CompensateAsync`.** It was not considered a concurrent caller of `EnqueueIfNotExistsAsync` at design time, but the failure window coincided with the watchdog's 1-minute poll cycle.

3. **Idempotency key scope.** `CompensationRefundRetry` idempotency keys were scoped to `OrderId + RetryRowId`, not `OrderId` alone. Two different retry rows for the same order produced two distinct keys — Stripe accepted both.

4. **No distributed lock on `CompensateAsync`.** The Redis saga lock is held during forward execution and `ResumeFromStepAsync`, but `CompensateAsync` was designed to be lock-free under the assumption that the retry table would serialize access. That assumption depended entirely on the deduplication working correctly.

---

## Impact

- **11 orders** refunded twice.
- **$4,180** over-refunded; fully reversed within ~4 hours.
- **0 customers** experienced net financial loss (Stripe holds funds; reversals completed before settlement).
- **~4 hours** of manual incident response.
- **Reputational:** 3 customers noticed duplicate refund emails and contacted support.

---

## Fix

A partial unique index was added to `FailedCompensationRetries`:

```sql
CREATE UNIQUE INDEX IX_FailedCompensationRetries_SagaId_Active
ON "FailedCompensationRetries" ("SagaId")
WHERE "Status" IN (0, 3);
```

This makes the race atomic at the database level. The second concurrent insert violates the constraint. The repository catches `UniqueViolation` and treats it as a no-op — exactly one retry row exists per saga.

`Completed` (1) and `Exhausted` (2) rows are excluded from the filter so historical records do not block future retries for the same saga.

---

## Why This Was Hard to See

Application-level "check then insert" patterns feel safe in single-threaded tests and low-concurrency development environments. The race window is narrow — milliseconds — and never reproduces locally. It only surfaces under the specific combination of: multiple replicas + simultaneous failure of many sagas + coincident poll cycles. Five years of development without Kubernetes horizontal scaling on this worker meant the concurrency assumption was never challenged.

---

## Action Items

| # | Action | Owner | Status |
|---|---|---|---|
| 1 | Deploy partial unique index (`AddFailedCompensationRetries` migration) | Backend | Done |
| 2 | Scope `CompensationRefundRetry` idempotency keys to `OrderId` only, not `OrderId + RetryRowId`, so Stripe deduplicates even if application-level deduplication fails | Backend | Planned |
| 3 | Audit all other "check then insert" patterns in the codebase for the same class of race | Backend | Planned |
| 4 | Add distributed Redis lock to `CompensateAsync` (same pattern as forward saga execution) as defense-in-depth | Backend | Planned |
| 5 | Add integration test that inserts two concurrent rows for the same saga and asserts exactly one is created | Backend | Planned |
| 6 | Add Stripe webhook alert threshold to PagerDuty for refund volume anomaly | Platform | Planned |

---

## Lessons Learned

**Application-level uniqueness guards are not uniqueness constraints.** Any "read then write" without a database-level constraint is a TOCTOU race. The fix is always a unique index (partial or full), not more careful application code.

**`SELECT FOR UPDATE SKIP LOCKED` serializes access to a single row, not to a logical entity.** If the same logical entity (a saga) can have multiple rows, SKIP LOCKED distributes them to different workers. Logical-entity uniqueness must be enforced before rows are created.

**Idempotency keys must be scoped to the business operation, not the internal retry row.** A retry row is an implementation detail. The business operation is "refund order X." The key should be `OrderId`, ensuring the payment provider deduplicates regardless of how many internal rows exist.
