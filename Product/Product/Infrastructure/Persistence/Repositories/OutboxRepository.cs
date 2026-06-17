using Application.Interfaces;
using Application.Models;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Persistence.Repositories;

internal sealed class OutboxRepository(ProductDbContext dbContext) : IOutboxRepository
{
    public async Task AddAsync(Guid messageId, string type, string content, DateTime occurredOn,
                               string aggregateId, CancellationToken ct = default)
    {
        var message = new OutboxMessage(messageId, type, content, occurredOn, aggregateId);
        await dbContext.OutboxMessages.AddAsync(message, ct);
        // SaveChanges called by ProductPersistenceService inside the transaction
    }

    public async Task<List<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize, int maxRetries, CancellationToken ct = default)
    {
        // FOR UPDATE SKIP LOCKED ensures each replica claims a distinct batch —
        // no duplicate publishes across multiple Product service instances
        if (dbContext.Database.CurrentTransaction is null)
            await dbContext.Database.BeginTransactionAsync(ct);

        return await dbContext.OutboxMessages
            .FromSqlRaw(
                """
                SELECT * FROM "OutboxMessages"
                WHERE "ProcessedOn" IS NULL
                  AND "RetryCount" < {0}
                ORDER BY "OccurredOn"
                LIMIT {1}
                FOR UPDATE SKIP LOCKED
                """,
                maxRetries,
                batchSize)
            .ToListAsync(ct);
    }

    public async Task<int> MarkRetryExhaustedMessagesAsProcessedAsync(int batchSize, int maxRetries, CancellationToken ct = default)
    {
        var messages = await dbContext.OutboxMessages
            .Where(m => m.ProcessedOn == null && m.RetryCount >= maxRetries)
            .OrderBy(m => m.OccurredOn)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var message in messages)
            message.MarkAsProcessed();

        if (messages.Count > 0)
            await dbContext.SaveChangesAsync(ct);

        return messages.Count;
    }

    public async Task MarkAsProcessedAsync(Guid messageId, CancellationToken ct = default)
    {
        var message = await dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is not null)
        {
            message.MarkAsProcessed();
            await dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task IncrementRetryCountAsync(Guid messageId, string error, CancellationToken ct = default)
    {
        var message = await dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is not null)
        {
            message.IncrementRetry(error);
            await dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteAsync(Guid messageId, CancellationToken ct = default)
    {
        await dbContext.OutboxMessages
            .Where(m => m.Id == messageId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteProcessedMessagesAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await dbContext.OutboxMessages
            .Where(m => m.ProcessedOn != null && m.ProcessedOn < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
