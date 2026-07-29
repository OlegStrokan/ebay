using Application.Interfaces;
using Application.Models;
using Domain.Entities;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public class DeadLetterRepository(AppDbContext dbContext, ILogger<DeadLetterRepository> logger) 
    : IDeadLetterRepository
{
    public async Task AddAsync(
        Guid messageId,
        string type,
        string content,
        DateTime occuredOn,
        string failureReason,
        int retryCount,
        string aggregateId,
        CancellationToken ct)
    {
        var deadLetterMessage = DeadLetterMessage.Create(
            messageId,
            type,
            content,
            occuredOn,
            failureReason,
            retryCount,
            aggregateId);

        dbContext.DeadLetterMessages.Add(deadLetterMessage);
        await dbContext.SaveChangesAsync(ct);

        logger.LogWarning(
            "Message {MessageId} ({Type}) moved to dead letter queue after {RetryCount} retries. Reason: {FailureReason}",
            messageId,
            type,
            retryCount,
            failureReason);
    }

    public async Task<IEnumerable<DeadLetterMessage>> GetAllAsync(
        int skip,
        int take,
        CancellationToken ct)
    {
        var messages = await dbContext.DeadLetterMessages
            .AsNoTracking()
            .Where(d => !d.IsResolved)
            .OrderByDescending(d => d.MovedToDeadLetterAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        logger.LogInformation(
            "Retrieved {Count} dead letter message (skip: {Skip}, take: {Take})",
            messages.Count,
            skip,
            take);

        return messages;
    }

    public async Task RetryAsync(Guid messageId, CancellationToken ct)
    {
        var deadLetterMessage = await dbContext.DeadLetterMessages
            .FirstOrDefaultAsync(d => d.Id == messageId, ct);

        if (deadLetterMessage == null)
        {
            logger.LogWarning(
                "Dead letter message {MessageId} not found for retry",
                messageId);
            throw new InvalidOperationException($"DeadLetterMessage {messageId} not found");
        }

        if (deadLetterMessage.IsResolved)
        {
            logger.LogWarning(
                "Dead letter message {MessageId} already resolved. Cannot retry.",
                messageId);
            throw new InvalidOperationException($"Dead letter message {messageId} already resolved");
        }
        
        deadLetterMessage.IncrementRetryCount();

        var outboxMessage = new OutboxMessage(
            Guid.NewGuid(),
            deadLetterMessage.Type,
            deadLetterMessage.Content,
            DateTime.UtcNow,
            deadLetterMessage.AggregateId);

        dbContext.OutboxMessages.Add(outboxMessage);

        deadLetterMessage.MarkAsResolved($"Requeued to outbox as message {outboxMessage.Id} for retry.");

        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation(
            "Dead letter message {MessageId} ({Type}) moved back to outbox as {OutboxMessageId} for retry (attempt {Attempt})",
            messageId,
            deadLetterMessage.Type,
            outboxMessage.Id,
            deadLetterMessage.DeadLetterRetryCount);
    }

    public async Task DeleteAsync(Guid messageId, CancellationToken ct)
    {
        var deadLetterMessage = await dbContext.DeadLetterMessages
            .FirstOrDefaultAsync(d => d.Id == messageId, ct);

        if (deadLetterMessage == null)
        {
            logger.LogWarning(
                "Dead letter message {MessageId} not found for deletion", messageId);
            return;
        }

        dbContext.DeadLetterMessages.Remove(deadLetterMessage);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task MarkAsResolvedAsync(Guid messageId, string resolutionNotes, CancellationToken ct)
    {
        var deadLetterMessage = await dbContext.DeadLetterMessages
            .FirstOrDefaultAsync(d => d.Id == messageId, ct);

        if (deadLetterMessage == null)
        {
            logger.LogWarning(
                "Dead letter message {MessageId} not found",
                messageId);
            return;
        }

        deadLetterMessage.MarkAsResolved(resolutionNotes);
        await dbContext.SaveChangesAsync(ct);
        
        logger.LogInformation(
            "Dead letter message {MessageId} marked as resolved. Notes: {Notes}",
            messageId,
            resolutionNotes);
    }
}