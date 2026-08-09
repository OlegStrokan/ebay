# Postmortem — Product outbox: self-inflicted deadlock and discarded bookkeeping

**Service:** Product
**Component:** `OutboxProcessor` + `OutboxRepository`
**Found:** 2026-08-08, while making the Product test suites pass with Docker available
**Symptoms:** 2 integration test failures, 6 E2E failures, `Npgsql … Timeout during reading attempt`

Two independent defects in the same code path. Either one alone breaks the outbox; together they
meant **every Product event was published to Kafka on every poll, forever, and never marked
processed**.

---

## Background — what the outbox does and why it locks

When Product saves a domain change it writes the event into an `OutboxMessages` table **in the same
database transaction** as the data. A background loop (`OutboxProcessor`) later reads unprocessed
rows, publishes them to Kafka, and marks them processed. That is what guarantees an event is not
lost if Kafka is unavailable at save time.

Product runs with more than one replica, and every replica runs that same loop. Two replicas must
not publish the same message twice, so the read claims its batch:

```sql
SELECT * FROM "OutboxMessages"
WHERE "ProcessedOn" IS NULL
  AND "RetryCount" < @maxRetries
ORDER BY "OccurredOn"
LIMIT @batchSize
FOR UPDATE SKIP LOCKED
```

`FOR UPDATE` locks the selected rows; `SKIP LOCKED` makes the other replica step over them instead
of blocking.

The critical Postgres detail: **a row lock lives exactly as long as its transaction.** To hold a
claim across the publish, the repository has to open a transaction and keep it open:

```csharp
if (dbContext.Database.CurrentTransaction is null)
    await dbContext.Database.BeginTransactionAsync(ct);
```

Both bugs follow from that single fact.

---

## Bug 1 — bookkeeping ran on a different connection than the claim

### What the code did

`ProcessBatchAsync` created a DI scope for the claim, then created **another scope inside the
parallel publish loop**:

```csharp
using var scope = serviceProvider.CreateScope();
var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

// DbContext A: opens a transaction, takes FOR UPDATE SKIP LOCKED row locks
var messages = await outboxRepository.GetUnprocessedMessagesAsync(_batchSize, _maxRetries, ct);

await Parallel.ForEachAsync(groups, options, async (group, groupCt) =>
{
    using var groupScope = serviceProvider.CreateScope();          // DbContext B
    var groupOutbox = groupScope.ServiceProvider.GetRequiredService<IOutboxRepository>();

    foreach (var message in group)
    {
        try
        {
            await eventPublisher.PublishRawAsync(/* … */, groupCt);
            await groupOutbox.MarkAsProcessedAsync(message.Id, groupCt);   // ← on B
        }
        catch (Exception ex)
        {
            await groupOutbox.IncrementRetryCountAsync(message.Id, ex.Message, groupCt);  // ← on B
        }
    }
});
```

A second DI scope means a second `DbContext`, which means a **second pooled connection**. So the
`UPDATE` that marks a message processed was issued from connection B against rows that connection A
had locked.

### Why that deadlocks

```
A: BEGIN
A: SELECT … FOR UPDATE SKIP LOCKED        -- rows now locked by A
B: UPDATE "OutboxMessages" SET "ProcessedOn" = …  WHERE "Id" = …
                                          -- blocks: A holds the row lock
A: cannot commit — still awaiting Parallel.ForEachAsync
B: still waiting on A's lock
```

The service deadlocked against itself. A's transaction only ends when `ProcessBatchAsync` returns,
which requires the parallel loop to finish, which requires B to finish, which requires A's lock.
Nothing breaks the cycle except the Npgsql command timeout (30s default), which then surfaces as a
"transient failure".

Postgres does not report this as a deadlock (`40P01`) because it is not a lock *cycle* between two
transactions — B is simply waiting on A forever. There is no victim to abort, so no error until the
client-side timeout fires.

### Why the symptoms looked like flakiness

- The Kafka publish **succeeded** — Kafka is not Postgres, and it was called before the blocked
  `UPDATE`. So messages really did go out.
- `ProcessedOn` stayed `null` and `RetryCount` stayed `0`, because the writes never landed.
- The E2E suite reported `Npgsql.NpgsqlException : Exception while reading from stream ---->
  System.TimeoutException : Timeout during reading attempt`, and Kafka assertions timed out after
  20s. It read like environment flakiness. It was deterministic.

### Fix

Publishing to Kafka in parallel is fine — that is network I/O with no shared `DbContext`. Only the
*database* writes have to be on the claim's connection. So the parallel region now records outcomes
instead of writing them, and all bookkeeping happens afterwards on the original repository:

```csharp
var outcomes = new ConcurrentQueue<(Guid Id, string? Error)>();

await Parallel.ForEachAsync(groups, options, async (group, groupCt) =>
{
    foreach (var message in group)
    {
        try
        {
            await eventPublisher.PublishRawAsync(/* … */, groupCt);
            outcomes.Enqueue((message.Id, null));
        }
        catch (Exception ex)
        {
            outcomes.Enqueue((message.Id, ex.Message));

            // Stop this aggregate's group so a later event can't overtake a failed one.
            break;
        }
    }
});

// Bookkeeping must run on the connection holding the FOR UPDATE SKIP LOCKED claim.
foreach (var (id, error) in outcomes)
{
    if (error is null)
        await outboxRepository.MarkAsProcessedAsync(id, ct);
    else
        await outboxRepository.IncrementRetryCountAsync(id, error, ct);
}
```

The inner `groupScope` is gone. Causal ordering per aggregate is preserved, cross-aggregate
parallelism is preserved, and the `break` was added so a failed message cannot be overtaken by a
later event for the same aggregate.

---

## Bug 2 — the claim transaction was never committed

### What the code did

`GetUnprocessedMessagesAsync` called `BeginTransactionAsync`. Nothing in the codebase ever called
`Commit` on it. The scope was disposed at the end of `ProcessBatchAsync`:

```csharp
using var scope = serviceProvider.CreateScope();
```

Disposing the scope disposes the `DbContext`, which disposes the open transaction — and an
uncommitted transaction **rolls back**.

### Why it matters independently of Bug 1

These are genuinely separate defects. Fixing only Bug 1 — moving the writes onto connection A —
would have produced an `UPDATE` that runs happily, is never committed, and is silently discarded on
dispose. `ProcessedOn` would still be `null` on the next poll, and the same messages would be
republished forever. The observable production symptom is identical; only the mechanism differs.

It is worth noting that `MarkRetryExhaustedMessagesAsProcessedAsync` was unaffected, because it runs
*before* `GetUnprocessedMessagesAsync` opens the transaction, so its `SaveChangesAsync` autocommits.
That is why the retry-exhaustion path appeared to work while everything else did not.

### Fix

Added an explicit commit to the repository contract and called it once the batch's bookkeeping is
done:

```csharp
// IOutboxRepository
// Commits the claim transaction opened by GetUnprocessedMessagesAsync; without it the
// FOR UPDATE SKIP LOCKED rows stay locked and every write in the batch is rolled back.
Task CommitClaimAsync(CancellationToken ct = default);
```

```csharp
// OutboxRepository
public async Task CommitClaimAsync(CancellationToken ct = default)
{
    if (dbContext.Database.CurrentTransaction is { } transaction)
        await transaction.CommitAsync(ct);
}
```

```csharp
// OutboxProcessor, after the bookkeeping loop
await outboxRepository.CommitClaimAsync(ct);
```

Committing also releases the row locks, which is what lets the *next* poll — and the other replica —
claim work again.

---

## How Order solves the same problem differently

Order's outbox never had either bug, because it does not hold a lock across the publish at all. Its
claim is a **committed state change**, not a held lock.

```csharp
// Order — OutboxRepository.ClaimUnprocessedMessagesAsync
var claimedIds = await dbContext.Database
    .SqlQueryRaw<Guid>(
        """
        UPDATE "OutboxMessages"
        SET "ClaimedAtUtc" = {0}
        WHERE "Id" IN (
            SELECT "Id" FROM "OutboxMessages"
            WHERE "ProcessedOnUtc" IS NULL
              AND ("ClaimedAtUtc" IS NULL OR "ClaimedAtUtc" < {1})
            ORDER BY "OccurredOnUtc"
            LIMIT {2}
            FOR UPDATE SKIP LOCKED
        )
        RETURNING "Id"
        """,
        now, staleThreshold, batchSize)
    .ToListAsync(ct);
```

The differences that matter:

| | Product (before fix) | Order |
|---|---|---|
| Claim mechanism | held row lock | `ClaimedAtUtc` column |
| Transaction | opened, held across publish, never committed | single autocommitted statement |
| Lock duration | the whole batch, including Kafka I/O | the one `UPDATE` |
| Bookkeeping connection | **must** be the claim's connection | any connection |
| Crash recovery | lock dies with the connection, row reappears | stale claims reclaimed after 5 min |

Because `FOR UPDATE SKIP LOCKED` sits inside the subquery of a single autocommitting `UPDATE`, the
lock is held only for the duration of that statement. Once it returns, the claim is durable in a
column rather than in transaction state. That is what makes Order's per-group scopes safe — there is
no lock left for a second connection to block on:

```csharp
await Parallel.ForEachAsync(groups, options, async (group, ct) =>
{
    using var groupScope = serviceProvider.CreateScope();   // safe here: no lock held
    var groupOutboxRepository = groupScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
    // …
});
```

It also survives a crash more gracefully. If a Product replica died mid-batch, the lock vanished
with the connection and the rows became claimable immediately — correct, but only because nothing
had been recorded. In Order a crash leaves `ClaimedAtUtc` set, and the
`ClaimedAtUtc < staleThreshold` predicate (5 minutes) is what makes the work reclaimable. That is a
deliberate, visible recovery window rather than an accident of connection lifetime.

**Order's approach is the better pattern**, and Product should eventually move to it: it needs no
long-lived transaction, imposes no constraint on which connection does the bookkeeping, and makes
claim state inspectable in the table. Adopting it in Product requires a `ClaimedAtUtc` column and a
migration, so it was out of scope for a test-fixing pass — the fix above is the minimal correct
change that keeps Product's existing shape.

---

## Verification

- `OutboxProcessor_ShouldPublishPendingMessage_AndMarkItProcessed` — `ProcessedOn` is now set.
- `OutboxProcessor_ShouldIncrementRetryCount_WhenPublishFails` — `RetryCount` now increments.
- All 6 Product E2E failures cleared, including the Kafka-delivery and Npgsql-timeout ones.
- Product solution: 410 tests passing, integration and E2E included.

## Related

- [`Payment/POSTMORTEM_stripe_eager_client_construction.md`](../Payment/POSTMORTEM_stripe_eager_client_construction.md) —
  the other production defect found in the same pass.
