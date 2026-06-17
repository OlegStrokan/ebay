using Application.Interfaces;
using Application.Models;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.Repositories;

public sealed class CompensationRefundRetryRepository(
    AppDbContext dbContext,
    ILogger<CompensationRefundRetryRepository> logger) : ICompensationRefundRetryRepository
{
    public async Task<CompensationRefundRetry> EnqueueIfNotExistsAsync(
        Guid orderId,
        string paymentId,
        decimal amount,
        string currency,
        string reason,
        CancellationToken cancellationToken)
    {
        var normalizedPaymentId = paymentId.Trim();

        var existing = await dbContext.CompensationRefundRetries
            .FirstOrDefaultAsync(
                x => x.OrderId == orderId
                     && x.PaymentId == normalizedPaymentId
                     && x.Status == CompensationRefundRetryStatus.Pending,
                cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Compensation refund retry already queued for order {OrderId}, payment {PaymentId}",
                orderId,
                normalizedPaymentId);
            return existing;
        }

        var retry = CompensationRefundRetry.Create(
            orderId,
            normalizedPaymentId,
            amount,
            currency,
            reason,
            DateTime.UtcNow);

        await dbContext.CompensationRefundRetries.AddAsync(retry, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return retry;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation(
                ex,
                "Detected concurrent enqueue for order {OrderId}, payment {PaymentId}. Returning existing pending row.",
                orderId,
                normalizedPaymentId);

            var concurrent = await dbContext.CompensationRefundRetries
                .FirstOrDefaultAsync(
                    x => x.OrderId == orderId
                         && x.PaymentId == normalizedPaymentId
                         && x.Status == CompensationRefundRetryStatus.Pending,
                    cancellationToken);

            if (concurrent is not null)
            {
                return concurrent;
            }

            throw;
        }
    }

    public async Task<CompensationRefundRetry?> GetPendingByOrderAndPaymentAsync(
        Guid orderId,
        string paymentId,
        CancellationToken cancellationToken)
    {
        var normalizedPaymentId = paymentId.Trim();

        return await dbContext.CompensationRefundRetries
            .FirstOrDefaultAsync(
                x => x.OrderId == orderId
                     && x.PaymentId == normalizedPaymentId
                     && (x.Status == CompensationRefundRetryStatus.Pending
                         || x.Status == CompensationRefundRetryStatus.InProgress),
                cancellationToken);
    }

    public async Task<IReadOnlyList<CompensationRefundRetry>> GetDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        return await dbContext.CompensationRefundRetries
            .Where(x => x.Status == CompensationRefundRetryStatus.Pending && x.NextAttemptAtUtc <= nowUtc)
            .OrderBy(x => x.NextAttemptAtUtc)
            .ThenBy(x => x.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CompensationRefundRetry>> ClaimDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // concurrent replicas never claim the same refund retry
        var claimedIds = await dbContext.Database
            .SqlQueryRaw<Guid>(
                """
                UPDATE "CompensationRefundRetries"
                SET "Status" = {0}, "UpdatedAtUtc" = {1}
                WHERE "Id" IN (
                    SELECT "Id" FROM "CompensationRefundRetries"
                    WHERE "Status" = {2} AND "NextAttemptAtUtc" <= {3}
                    ORDER BY "NextAttemptAtUtc", "CreatedAtUtc"
                    LIMIT {4}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING "Id"
                """,
                (int)CompensationRefundRetryStatus.InProgress,
                nowUtc,
                (int)CompensationRefundRetryStatus.Pending,
                nowUtc,
                batchSize)
            .ToListAsync(cancellationToken);

        if (claimedIds.Count == 0)
            return [];

        return await dbContext.CompensationRefundRetries
            .Where(x => claimedIds.Contains(x.Id))
            .OrderBy(x => x.NextAttemptAtUtc)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(CompensationRefundRetry retry, CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(retry);
        if (entry.State == EntityState.Detached)
        {
            dbContext.CompensationRefundRetries.Update(retry);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
