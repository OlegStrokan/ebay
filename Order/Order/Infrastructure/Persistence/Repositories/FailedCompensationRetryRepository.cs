using Application.Interfaces;
using Application.Models;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.Repositories;

public sealed class FailedCompensationRetryRepository(
    AppDbContext dbContext,
    ILogger<FailedCompensationRetryRepository> logger) : IFailedCompensationRetryRepository
{
    public async Task EnqueueIfNotExistsAsync(
        Guid sagaId,
        string sagaType,
        string lastFailedStep,
        string lastError,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.FailedCompensationRetries
            .FirstOrDefaultAsync(
                x => x.SagaId == sagaId
                     && (x.Status == FailedCompensationRetryStatus.Pending
                         || x.Status == FailedCompensationRetryStatus.InProgress),
                cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "FailedCompensationRetry already active for saga {SagaId} (Status={Status}). Skipping enqueue.",
                sagaId, existing.Status);
            return;
        }

        var retry = FailedCompensationRetry.Create(sagaId, sagaType, lastFailedStep, lastError, DateTime.UtcNow);
        await dbContext.FailedCompensationRetries.AddAsync(retry, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert by another process — idempotent, ignore
            logger.LogInformation(
                ex,
                "Concurrent FailedCompensationRetry enqueue for saga {SagaId}. Ignoring.",
                sagaId);
        }
    }

    public async Task<IReadOnlyList<FailedCompensationRetry>> ClaimDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var claimedIds = await dbContext.Database
            .SqlQueryRaw<Guid>(
                """
                UPDATE "FailedCompensationRetries"
                SET "Status" = {0}, "UpdatedAtUtc" = {1}
                WHERE "Id" IN (
                    SELECT "Id" FROM "FailedCompensationRetries"
                    WHERE "Status" = {2} AND "NextAttemptAtUtc" <= {3}
                    ORDER BY "NextAttemptAtUtc", "CreatedAtUtc"
                    LIMIT {4}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING "Id"
                """,
                (int)FailedCompensationRetryStatus.InProgress,
                nowUtc,
                (int)FailedCompensationRetryStatus.Pending,
                nowUtc,
                batchSize)
            .ToListAsync(cancellationToken);

        if (claimedIds.Count == 0)
            return [];

        return await dbContext.FailedCompensationRetries
            .Where(x => claimedIds.Contains(x.Id))
            .OrderBy(x => x.NextAttemptAtUtc)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<FailedCompensationRetry?> GetBySagaIdAsync(
        Guid sagaId,
        CancellationToken cancellationToken)
    {
        return await dbContext.FailedCompensationRetries
            .Where(x => x.SagaId == sagaId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(FailedCompensationRetry retry, CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(retry);
        if (entry.State == EntityState.Detached)
        {
            dbContext.FailedCompensationRetries.Update(retry);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
