using Infrastructure.Messaging;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> outboxOptions,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private DateTime _lastCleanupAt = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);

                if (DateTime.UtcNow - _lastCleanupAt >=
                    TimeSpan.FromMilliseconds(outboxOptions.Value.CleanupIntervalMs))
                {
                    await CleanupProcessedMessagesAsync(stoppingToken);
                    _lastCleanupAt = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error while processing inventory outbox batch.");
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(outboxOptions.Value.PollIntervalMs),
                stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();

        var batch = await dbContext.OutboxMessages
            .Where(x => x.ProcessedAtUtc == null && x.RetryCount < outboxOptions.Value.MaxRetries)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(outboxOptions.Value.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
            return;

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                message.ProcessedAtUtc = DateTime.UtcNow;
                message.LastError = string.Empty;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;

                logger.LogError(
                    ex,
                    "Failed to publish outbox message. MessageId={MessageId}, RetryCount={RetryCount}",
                    message.OutboxMessageId,
                    message.RetryCount);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupProcessedMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(outboxOptions.Value.RetentionHours);

        var deleted = await dbContext.OutboxMessages
            .Where(x => x.ProcessedAtUtc != null && x.ProcessedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Deleted {Count} processed outbox messages older than {RetentionHours}h.",
                deleted,
                outboxOptions.Value.RetentionHours);
        }
    }
}
