using Application.Interfaces;
using Domain.Interfaces;
using Infrastructure.Messaging;
using Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

internal sealed class MoneyEventDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MoneyEventOptions> moneyEventOptions,
    ILogger<MoneyEventDeliveryWorker> logger) : BackgroundService
{
    private readonly MoneyEventOptions _moneyEventOptions = moneyEventOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in money event delivery worker");
            }

            var pause = NormalizePositive(_moneyEventOptions.PollIntervalSeconds, 5);
            await Task.Delay(TimeSpan.FromSeconds(pause), stoppingToken);
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var moneyEventRepository = scope.ServiceProvider.GetRequiredService<IOutboundMoneyEventRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IMoneyEventDispatcher>();

        var now = clock.UtcNow;
        var batchSize = NormalizePositive(_moneyEventOptions.BatchSize, 100);

        var moneyEvents = await moneyEventRepository.GetPendingAsync(now, batchSize, cancellationToken);
        if (moneyEvents.Count == 0)
        {
            return 0;
        }

        var maxAttempts = NormalizePositive(_moneyEventOptions.MaxAttempts, 8);
        var changed = 0;

        foreach (var moneyEvent in moneyEvents)
        {
            if (!moneyEvent.CanAttempt(now))
            {
                continue;
            }

            var attemptedAt = clock.UtcNow;
            var delivery = await dispatcher.DispatchAsync(moneyEvent, cancellationToken);

            if (delivery.Succeeded)
            {
                moneyEvent.MarkDelivered(attemptedAt);
                await moneyEventRepository.UpdateAsync(moneyEvent, cancellationToken);
                changed++;
                continue;
            }

            var errorMessage = string.IsNullOrWhiteSpace(delivery.Error)
                ? "Money event delivery failed."
                : delivery.Error;

            var currentAttempt = moneyEvent.AttemptCount + 1;

            if (currentAttempt >= maxAttempts)
            {
                moneyEvent.MarkPermanentFailure(errorMessage, attemptedAt);

                logger.LogError(
                    "Money event {EventId} of type {EventType} was not delivered after {AttemptCount} attempts. The ledger will drift until it is replayed.",
                    moneyEvent.EventId,
                    moneyEvent.EventType,
                    currentAttempt);
            }
            else
            {
                var delay = CalculateRetryDelay(currentAttempt);
                moneyEvent.MarkAttemptFailed(errorMessage, attemptedAt.Add(delay), attemptedAt);
            }

            await moneyEventRepository.UpdateAsync(moneyEvent, cancellationToken);
            changed++;
        }

        if (changed > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        logger.LogDebug(
            "Money event delivery cycle completed. Loaded={LoadedCount}, Updated={UpdatedCount}",
            moneyEvents.Count,
            changed);

        return changed;
    }

    private TimeSpan CalculateRetryDelay(int attemptNumber)
    {
        var baseSeconds = NormalizePositive(_moneyEventOptions.BaseRetryDelaySeconds, 5);
        var maxSeconds = Math.Max(baseSeconds, NormalizePositive(_moneyEventOptions.MaxRetryDelaySeconds, 300));

        var safeAttempt = Math.Max(1, attemptNumber);
        var growth = Math.Pow(2, safeAttempt - 1);
        var delay = Math.Min(maxSeconds, baseSeconds * growth);

        return TimeSpan.FromSeconds(delay);
    }

    private static int NormalizePositive(int value, int fallback)
    {
        return value > 0 ? value : fallback;
    }
}
