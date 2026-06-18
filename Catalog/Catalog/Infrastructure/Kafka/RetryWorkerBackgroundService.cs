using System.Text.Json;
using Application.Consumers;
using Application.RetryStore;
using Infrastructure.RetryStore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Kafka;

public sealed class RetryWorkerBackgroundService(
    IServiceProvider serviceProvider,
    IOptions<RetryStoreOptions> retryStoreOptions,
    ILogger<RetryWorkerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var opts = retryStoreOptions.Value;
        var pollInterval = TimeSpan.FromSeconds(opts.WorkerPollIntervalSeconds);

        logger.LogInformation(
            "Retry worker started. Poll interval: {Interval}s, retry limit: {Limit}, batch size: {Batch}",
            opts.WorkerPollIntervalSeconds, opts.WorkerRetryLimit, opts.WorkerBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueRecords(opts, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retry worker encountered an unexpected error");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }

        logger.LogInformation("Retry worker stopped");
    }

    private async Task ProcessDueRecords(RetryStoreOptions opts, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var retryStore = scope.ServiceProvider.GetRequiredService<IRetryStore>();

        var dueRecords = await retryStore.GetDueRecordsAsync(opts.WorkerBatchSize, ct);
        if (dueRecords.Count == 0) return;

        logger.LogInformation("Retry worker processing {Count} due records", dueRecords.Count);

        foreach (var record in dueRecords)
        {
            if (ct.IsCancellationRequested) break;

            await ProcessSingleRecord(record, retryStore, opts, ct);
        }
    }

    private async Task ProcessSingleRecord(
        RetryRecord record, IRetryStore retryStore, RetryStoreOptions opts, CancellationToken ct)
    {
        try
        {
            var wrapper = JsonSerializer.Deserialize<EventWrapper>(record.Payload);
            if (wrapper is null)
            {
                logger.LogError(
                    "Retry record {RecordId} has invalid payload - moving to dead letter",
                    record.Id);

                await retryStore.MarkDeadLetterAsync(
                    record.Id, "Failed to deserialize stored payload", "DeserializationError", ct);
                return;
            }

            await DispatchAsync(record.EventType, wrapper.Payload, ct);

            await retryStore.MarkSucceededAsync(record.Id, ct);

            logger.LogInformation(
                "Retry record {RecordId} projected successfully (event={EventType})",
                record.Id, record.EventType);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var newRetryCount = record.RetryCount + 1;

            if (newRetryCount >= opts.WorkerRetryLimit)
            {
                // Exhausted - move to dead letter
                logger.LogError(
                    ex,
                    "Retry record {RecordId} exceeded worker retry limit ({Limit}) — moving to DeadLetter",
                    record.Id, opts.WorkerRetryLimit);

                await retryStore.MarkDeadLetterAsync(
                    record.Id,
                    Truncate(ex.Message, 2000),
                    ex.GetType().FullName,
                    ct);
            }
            else
            {
                var nextRetry = DateTime.UtcNow.AddMinutes(3 + Random.Shared.Next(0, 120));

                logger.LogWarning(
                    ex,
                    "Retry record {RecordId} failed (attempt {Attempt}/{Limit}), rescheduling for {NextRetry}",
                    record.Id, newRetryCount, opts.WorkerRetryLimit, nextRetry);

                await retryStore.RescheduleAsync(
                    record.Id,
                    newRetryCount,
                    nextRetry,
                    Truncate(ex.Message, 2000),
                    ex.GetType().FullName,
                    ct);
            }
        }
    }

    private async Task DispatchAsync(string eventType, JsonElement payload, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var consumers = scope.ServiceProvider.GetRequiredService<IEnumerable<IProductEventConsumer>>();

        var handler = consumers.FirstOrDefault(c => c.EventType == eventType);
        if (handler is null)
        {
            logger.LogDebug("No consumer registered for event type '{EventType}' — skipping in retry worker", eventType);
            return;
        }

        await handler.ConsumeAsync(payload, ct);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null
        : value.Length <= maxLength ? value
        : value[..maxLength];
}
