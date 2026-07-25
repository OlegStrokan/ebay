# Post-Mortem: Orders Persisted as Paid with Unconfirmed Inventory During Inventory Outage

**Date:** 2026-06-27  
**Severity:** P1 — data integrity, customer experience  
**Duration:** ~18 minutes of customer-visible impact; ~30 minutes of inventory count divergence  
**Services affected:** Order Service, Inventory Service, Email Service  
**Written by:** Engineering

---

## Summary

A brief Inventory service database outage caused `ConfirmReservationAsync` to time out inside `UpdateOrderStatusStep`. Because the step called `order.Pay()` — persisting the order as `Paid` in the event store and publishing an `OrderPaid` event via the outbox — **before** calling `ConfirmReservationAsync`, the step failure triggered saga compensation against an already-`Paid` order. The outbox processor published the `OrderPaid` event before compensation cancelled the order, so the Email service sent payment-confirmation emails for orders that were about to be cancelled. Compensation's `ReleaseReservationAsync` also failed while Inventory was still down, leaving 31 reservations stuck in `Active` state. Those reservations held stock for up to 30 minutes until the `ReservationExpiryProcessor` released them, causing those SKUs to appear partially out of stock during peak traffic.

No customers were charged — payment capture (step 6) had not been reached, so only an authorization hold existed, which compensation voided successfully. The customer-visible harm was 31 confusing email sequences: "Your order is confirmed" followed minutes later by "Your order has been cancelled."

---

## Timeline

| Time (UTC) | Event |
|---|---|
| 11:04 | Inventory service PostgreSQL primary begins experiencing lock contention from a long-running migration on the `inventory_reservations` table. Write latency climbs from <5 ms to 8–12 s. |
| 11:06 | First `ConfirmReservationAsync` gRPC call from Order service times out (30 s deadline). `UpdateOrderStatusStep` catch-all handler returns `Fail(...)`. Compensation begins for `ord-4d71...`. |
| 11:06 | `order.Pay()` had already been called and persisted for `ord-4d71...` via `OrderPersistenceService.UpdateOrderAsync`. The `OrderPaid` domain event is in the outbox. |
| 11:06 | `OutboxProcessor` (2 s poll cycle, currently mid-poll) picks up the `OrderPaid` event and publishes it to Kafka `order.events`. |
| 11:07 | Email service consumes `OrderPaid` event. Sends "Your payment was successful — your order is on its way" email to customer for `ord-4d71...`. |
| 11:07 | Compensation for `ord-4d71...` reaches `CancelOrderOnFailureStep` (step 0). `order.Cancel()` called, `OrderCancelled` event persisted and published. |
| 11:07 | Email service consumes `OrderCancelled` event. Sends "Your order has been cancelled" email to same customer. Customer receives both emails within 47 seconds. |
| 11:07 | Compensation reaches `ReserveInventoryStep.CompensateAsync`. Calls `ReleaseReservationAsync`. Inventory is still degraded — call times out. Compensation logs error, creates intervention ticket `#5812`, continues. Reservation for `ord-4d71...` remains `Active` in Inventory DB. |
| 11:07–11:22 | 31 orders in the batch currently at step 4 of their sagas follow the same path. All 31 are persisted as `Paid`, all 31 have `OrderPaid` events published, all 31 are then cancelled by compensation. All 31 `ReleaseReservationAsync` calls fail. 31 intervention tickets created (`#5812–#5842`). |
| 11:08 | Authorization holds for all 31 orders voided successfully via `CancelAuthorizationAsync` (Payment service unaffected). No customers charged. |
| 11:09 | On-call receives PagerDuty alert: intervention ticket volume spike (`saga.intervention_tickets_created_total` threshold crossed). |
| 11:12 | Inventory migration completes. Lock contention resolves. Write latency returns to normal. |
| 11:16 | On-call identifies pattern: all 31 orders share `UpdatedAt ≈ 11:06–11:07`, `Status = Cancelled`, 0 `CompensationRefundRetry` rows (correct — capture never happened), 31 open intervention tickets for failed `ReleaseReservationAsync`. |
| 11:22 | On-call manually calls `ReleaseReservationAsync` for all 31 reservation IDs via admin endpoint. All succeed. Inventory counts restored. Intervention tickets closed. |
| 11:25 | Stock levels for 4 affected SKUs normalised. No customer-facing out-of-stock shown (stock was reserved but listed as available above threshold). |
| 11:31 | Last customer support ticket received regarding the confusing email pair. Total: 8 support contacts. |

---

## Root Cause

`UpdateOrderStatusStep.ExecuteAsync` called `order.Pay()` and persisted the order as `Paid` **before** calling `ConfirmReservationAsync`:

```csharp
// BEFORE FIX — incorrect ordering
await orderPersistenceService.UpdateOrderAsync(order => {
    order.Pay(paymentId);   // ← persisted first; OrderPaid event written to outbox
});

await inventoryGateway.ConfirmReservationAsync(reservationId, ct);  // ← fails here
```

When `ConfirmReservationAsync` threw, the step returned `Fail`. Saga compensation ran against an order already in `Paid` state, with an `OrderPaid` outbox event already queued for publishing. Because the `OutboxProcessor` polls every 2 seconds and was mid-cycle, the event was published before compensation cancelled the order.

The result was a window — measured in seconds — where the system simultaneously believed the order was `Paid` (event store, outbox, Kafka) and the saga was compensating it toward `Cancelled`. The Email service, consuming from Kafka, acted on the `Paid` event faithfully.

---

## Contributing Factors

1. **Outbox publishes before compensation completes.** The `OutboxProcessor` runs on an independent 2-second timer. There is no mechanism to retract an already-queued outbox message once the saga starts compensating. Once `OrderPaid` was in the outbox, it was guaranteed to be published regardless of what happened next.

2. **Compensation's `ReleaseReservationAsync` targets the same degraded service.** `ConfirmReservationAsync` failed because Inventory was slow. `ReleaseReservationAsync` in compensation calls the same service. During the 8-minute Inventory outage, both calls failed, leaving reservations stuck. The intervention ticket mechanism created the tickets but the on-call workflow for bulk reservation release was not documented.

3. **No alert on "order transitioned Paid → Cancelled within N seconds."** A status regression of this kind is always a sign of a failed saga step between steps 4 and 7. No metric tracked this transition pair. On-call discovered it by correlating intervention ticket IDs with order update timestamps.

4. **Inventory migration ran during peak hours without a maintenance window.** The DDL lock that caused write contention was from an uncoordinated schema change against the `inventory_reservations` table.

---

## Impact

| Metric | Value |
|---|---|
| Orders affected | 31 |
| Customers charged | 0 (authorization voided; capture step not reached) |
| Customers who received confusing email pair | 31 |
| Support contacts | 8 |
| SKUs with temporarily incorrect stock count | 4 |
| Duration of stock count divergence | ~10 min (manual fix; would have been 30 min via expiry) |
| Intervention tickets auto-created | 31 |
| Manual operator actions required | 1 bulk reservation release |

---

## What Went Well

- Authorization holds were voided correctly by compensation. No customer was charged.
- Intervention ticket alerts paged on-call within 3 minutes of the first failure.
- `ReservationExpiryProcessor` would have self-healed within 30 minutes with no intervention.
- The 31 affected orders were identifiable within 7 minutes using `SagaStatus + UpdatedAt + intervention ticket range`.
- `CancelAuthorizationAsync` (Payment service) was unaffected by the Inventory outage and completed cleanly for all 31 orders.

## What Went Wrong

- `order.Pay()` was persisted before `ConfirmReservationAsync` succeeded. The `OrderPaid` event was in the outbox before the saga knew whether inventory could be confirmed. This is the root ordering bug.
- Once `OrderPaid` is in the outbox it cannot be retracted. The Email service has no concept of a "pending confirmation" — it acts on every `OrderPaid` event immediately.
- `ReleaseReservationAsync` in compensation calls the same Inventory endpoint that had just failed. No circuit breaker or fallback existed at the gateway level to skip release and instead schedule a deferred retry.
- The operator runbook had no entry for "bulk reservation release after failed compensation." On-call had to discover and execute the admin endpoint manually.
- No alert existed for the `Paid → Cancelled` state regression on an order.

---

## Fix

`UpdateOrderStatusStep.ExecuteAsync` now confirms inventory **before** persisting the order as `Paid`:

```csharp
// AFTER FIX — correct ordering
if (!context.ReservationConfirmed)
{
    await inventoryGateway.ConfirmReservationAsync(reservationId, ct);
    context.ReservationConfirmed = true;    // idempotency flag persisted before Pay
}

await orderPersistenceService.UpdateOrderAsync(order => {
    order.Pay(paymentId);   // ← only reached if confirm succeeded
});
```

A `context.ReservationConfirmed` flag is set after a successful confirm and persisted in the saga context. On saga resume (e.g., after a `WaitForEvent` from `AwaitPaymentConfirmationStep`) the confirm is skipped if already completed, and only the `Pay` persist is retried.

If `ConfirmReservationAsync` now fails, `order.Pay()` is never called, no `OrderPaid` event is written to the outbox, and compensation cancels an order still in `Pending` state — consistent with what the customer was told.

---

## Action Items

| # | Action | Owner | Priority | Status |
|---|---|---|---|---|
| 1 | Fix `UpdateOrderStatusStep`: confirm inventory before marking Paid; add `ReservationConfirmed` idempotency flag | Order team | P0 | ✅ Done (v2.14.4) |
| 2 | Add alert: order status regression `Paid → Cancelled` within a configurable window (suggest 60 s) | SRE | P1 | 🔲 Backlog |
| 3 | Add runbook entry: bulk `ReleaseReservationAsync` via admin endpoint after compensation failure storm | On-call | P1 | 🔲 Backlog |
| 4 | Add circuit breaker / retry scheduling to `ReserveInventoryStep.CompensateAsync` so a down Inventory service results in a deferred release rather than an open intervention ticket | Order team | P2 | 🔲 Backlog |
| 5 | Enforce maintenance window policy for DDL changes on high-write tables (`inventory_reservations`, `saga_states`, `outbox_messages`) | Platform/DBA | P1 | 🔲 Backlog |
| 6 | Investigate transactional outbox "hold" mechanism: delay publishing `OrderPaid` until saga step 7 (`CompleteOrder`) to avoid the compensation window entirely | Order team | P2 | 🔲 Backlog |

---

## Lessons Learned

**The outbox is a one-way door.** Once an event is in the outbox it will be delivered. Any step that writes a state-changing domain event to the outbox must be certain that state is durable and correct before writing it. Writing `OrderPaid` before confirming inventory was an implicit bet that the confirm would succeed — a bet the outbox processor settled before the result was known.

**Operation ordering within a saga step is part of the correctness contract.** The step name "UpdateOrderStatus" describes the outcome, not the sequence. The sequence — confirm first, then pay — is what makes the outcome safe. Both orderings "work" in the happy path; only one is correct under partial failure.

**Compensation calling the same degraded service that just caused the failure is a common blind spot.** When `ConfirmReservationAsync` times out, compensation's `ReleaseReservationAsync` will likely time out too. The compensation path needs its own resilience strategy, separate from the forward path's.
