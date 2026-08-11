# Region Affinity Decision (CreateOrder)

Date: 2026-03-24
Owner: Order Service Team
Status: Accepted (current)

## 1. Current Approach (Implemented)

We use **Region Affinity by Customer** for `CreateOrder` write ownership.

How it works:
- Deterministic owner region is computed from `CustomerId` (hash-based mapping).
- Each deployed region has `WriteRouting` config (`Enabled`, `CurrentRegion`, `Regions`).
- On create-order command:
  - if current region is owner -> continue normal flow.
  - if current region is not owner -> reject early with owner-region hint.
- Existing local idempotency (`IdempotencyKey`) remains in place.

Current goal:
- Reduce cross-region duplicate probability with low latency overhead and low implementation complexity.

## 2. Why We Skipped Other Approaches For Now

### Skipped: Global Idempotency Store (for now)
Reason:
- Higher engineering complexity (new global consistency boundary/service).
- Additional infrastructure and operational cost.
- Additional request-path hop (latency impact).
- Current business tolerance accepts small duplicate probability (~0.1%).

### Skipped: Sync Replication Requirement (for now)
Reason:
- Can increase write latency and operational complexity.
- Does not fully solve timeout-after-commit and routing/failover mistakes by itself.
- Not required for current business risk threshold.

### Skipped: Full Failover/Fencing Discipline Automation (for now)
Reason:
- Requires cross-team operational maturity and strict automation/runbooks.
- More implementation/operational effort than currently justified.
- Deferred while traffic/risk profile remains acceptable.

## 3. Risk We Explicitly Accept

With region affinity only, duplicates are still possible in edge failure windows:
- Partial-region outages and retry reroutes.
- Stale routing caches during ownership/failover changes.
- Dual-writer incident windows caused by failover mistakes.

Business acceptance:
- Rare duplicates are acceptable if compensation/reconciliation resolves them.

## 4. Existing Guardrails We Keep

- Mandatory idempotency key at API boundary.
- Early owner-region rejection in non-owner region.
- Saga compensation paths and operational recovery mechanisms.
- Test coverage across unit/integration/e2e for ownership guard behavior.

## 5. Future Combined Target (When Risk/Scale Requires)

Target sequence for stronger correctness and resilience:

### Phase A: Harden Operations (Failover/Fencing Discipline)
- Enforce strict writer switch order: **fence old writer first**, then promote new writer.
- Ownership version/epoch controls for write authority.
- Short routing cache TTL + active invalidation.
- Post-switch verification checks (old rejects, new accepts).

### Phase B: Add Global Idempotency For Critical Commands
- Start with `CreateOrder` only.
- Use atomic global `reserve-if-absent(idempotencyKey)`.
- States: `Pending` -> `Completed(orderId)`.
- Add reconciliation worker for crash gap between local commit and global completion mark.

### Phase C: Re-evaluate Replication Mode
- Consider synchronous replication only if RPO/RTO/compliance needs justify latency/cost.
- Treat sync replication as an additional tool, not sole duplicate-prevention mechanism.

## 6. Trigger Conditions To Start Future Phases

We move beyond region-affinity-only when one or more conditions are true:
- Duplicate incident rate exceeds business threshold.
- Support/finance cost of compensation becomes material.
- Multi-region failovers become frequent/complex.
- Stronger correctness is required by contracts/compliance.

## 7. Decision Summary

Current decision is intentional and pragmatic:
- **Use Region Affinity by Customer now**.
- **Accept small residual duplicate risk**.
- **Defer global idempotency + full fencing + sync replication** until business/operational triggers justify cost and complexity.
