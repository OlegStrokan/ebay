using Application.Interfaces;
using Application.Models;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public sealed class PplPendingBookingRepository(
    AppDbContext dbContext,
    ILogger<PplPendingBookingRepository> logger) : IPplPendingBookingRepository
{
    public async Task<PplPendingBooking> EnqueueAsync(
        Guid orderId,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.PplPendingBookings
            .FirstOrDefaultAsync(
                x => x.OrderId == orderId
                     && (x.Status == PplPendingBookingStatus.Pending
                         || x.Status == PplPendingBookingStatus.InProgress),
                cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "PPL pending booking already queued for order {OrderId}. Existing ReferenceId={ExistingRef}, new ReferenceId={NewRef}",
                orderId, existing.ReferenceId, referenceId);
            return existing;
        }

        var booking = PplPendingBooking.Create(orderId, referenceId);
        await dbContext.PplPendingBookings.AddAsync(booking, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "PPL pending booking enqueued. OrderId={OrderId}, ReferenceId={ReferenceId}",
            orderId, referenceId);

        return booking;
    }

    public async Task<IReadOnlyList<PplPendingBooking>> ClaimDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // Atomic claim: concurrent replicas never process the same booking row.
        var claimedIds = await dbContext.Database
            .SqlQueryRaw<Guid>(
                """
                UPDATE "PplPendingBookings"
                SET "Status" = {0}, "UpdatedAtUtc" = {1}
                WHERE "Id" IN (
                    SELECT "Id" FROM "PplPendingBookings"
                    WHERE "Status" = {2} AND "NextRetryAtUtc" <= {3}
                    ORDER BY "NextRetryAtUtc", "CreatedAtUtc"
                    LIMIT {4}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING "Id"
                """,
                (int)PplPendingBookingStatus.InProgress,
                nowUtc,
                (int)PplPendingBookingStatus.Pending,
                nowUtc,
                batchSize)
            .ToListAsync(cancellationToken);

        if (claimedIds.Count == 0)
            return [];

        return await dbContext.PplPendingBookings
            .Where(x => claimedIds.Contains(x.Id))
            .OrderBy(x => x.NextRetryAtUtc)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(PplPendingBooking booking, CancellationToken cancellationToken)
    {
        dbContext.PplPendingBookings.Update(booking);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
