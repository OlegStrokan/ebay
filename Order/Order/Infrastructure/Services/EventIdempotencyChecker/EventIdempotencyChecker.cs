using Domain.Entities;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Services.EventIdempotencyChecker;

// prevent duplicates for at-least-once kafka settings
// inbox pattern
public class EventIdempotencyChecker(
    AppDbContext  dbContext, 
    ILogger<EventIdempotencyChecker> logger) : IEventIdempotencyChecker{
    public async Task<bool> HasBeenProcessedAsync(Guid eventId, CancellationToken ct = default)
    {
        var exists = await dbContext.ProcessedEvents
            .AnyAsync(e => e.EventId == eventId, ct);

        if (exists)
        {
            logger.LogDebug(
                "Event {EventId} has already been processed",
                eventId);
        }

        return exists;
    }

    public async Task<bool> MarkAsProcessedAsync(Guid eventId, string eventType, CancellationToken ct = default)
    {
        try
        {
            // Use SERIALIZABLE isolation to prevent concurrent duplicate processing
            using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            // Double-check that event hasn't been processed since our check
            var alreadyProcessed = await dbContext.ProcessedEvents
                .AnyAsync(e => e.EventId == eventId, ct);

            if (alreadyProcessed)
            {
                logger.LogInformation(
                    "Event {EventId} was marked between check and insert. Skipping.",
                    eventId);
                await tx.RollbackAsync(ct);
                return false;
            }

            var processedEvent = ProcessedEvent.Create(
                eventId,
                eventType,
                "OrderService");

            dbContext.ProcessedEvents.Add(processedEvent);
            await dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogDebug(
                "Marked event {EventId} ({EventType}) as processed",
                eventId,
                eventType);

            return true;
        }

        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // another consumer already marked it - this is ok (race condition, but ok)
            logger.LogInformation(
                "Event {EventId} was already marked as processed by another consumer",
                eventId);
            return false;
        }
    }
}