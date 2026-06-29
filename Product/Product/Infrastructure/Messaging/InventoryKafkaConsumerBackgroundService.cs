using System.Text;
using System.Text.Json;
using Application.Consumers;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Messaging;

public sealed class InventoryKafkaConsumerBackgroundService(
    IOptions<KafkaOptions> kafkaOptions,
    IServiceProvider serviceProvider,
    ILogger<InventoryKafkaConsumerBackgroundService> logger) : BackgroundService
{
    private const int MaxImmediateRetries = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var options = kafkaOptions.Value;

        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.InventoryConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            IsolationLevel = IsolationLevel.ReadCommitted,
        };

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        using var dlqProducer = new ProducerBuilder<string, string>(producerConfig).Build();
        consumer.Subscribe(options.InventoryEventsTopic);

        logger.LogInformation(
            "Inventory Kafka consumer started. Topic: {Topic}, GroupId: {GroupId}",
            options.InventoryEventsTopic, options.InventoryConsumerGroupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;

            try
            {
                result = consumer.Consume(TimeSpan.FromMilliseconds(1000));
                if (result is null) continue;

                var eventType = GetHeader(result, "event-type");
                if (string.IsNullOrEmpty(eventType))
                {
                    logger.LogWarning(
                        "Message at Partition {P} Offset {O} has no event-type header — skipping",
                        result.Partition.Value, result.Offset.Value);
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                    continue;
                }

                var wrapper = JsonSerializer.Deserialize<EventWrapper>(result.Message.Value);
                if (wrapper is null)
                {
                    logger.LogWarning(
                        "Failed to deserialize EventWrapper for '{EventType}' — skipping", eventType);
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                    continue;
                }

                var success = await TryConsumeWithRetries(eventType, wrapper, stoppingToken);
                if (!success)
                {
                    logger.LogError(
                        "All retries exhausted for '{EventType}' at Partition {P} Offset {O} — publishing to DLQ",
                        eventType, result.Partition.Value, result.Offset.Value);

                    await PublishToDlqAsync(dlqProducer, options.InventoryDlqTopic, result, stoppingToken);
                }

                consumer.StoreOffset(result);
                consumer.Commit(result);
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in inventory consumer loop");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        consumer.Close();
        logger.LogInformation("Inventory Kafka consumer stopped");
    }

    private async Task PublishToDlqAsync(
        IProducer<string, string> dlqProducer,
        string dlqTopic,
        ConsumeResult<string, string> original,
        CancellationToken ct)
    {
        try
        {
            var headers = new Headers();
            if (original.Message.Headers is not null)
            {
                foreach (var header in original.Message.Headers)
                {
                    headers.Add(header.Key, header.GetValueBytes());
                }
            }

            headers.Add("dlq-source-topic", Encoding.UTF8.GetBytes(original.Topic));
            headers.Add("dlq-source-partition", Encoding.UTF8.GetBytes(original.Partition.Value.ToString()));
            headers.Add("dlq-source-offset", Encoding.UTF8.GetBytes(original.Offset.Value.ToString()));
            headers.Add("dlq-failed-at", Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")));

            var dlqMessage = new Message<string, string>
            {
                Key = original.Message.Key,
                Value = original.Message.Value,
                Headers = headers
            };

            await dlqProducer.ProduceAsync(dlqTopic, dlqMessage, ct);

            logger.LogWarning(
                "Published failed inventory event to DLQ topic '{DlqTopic}' (source partition {P} offset {O})",
                dlqTopic, original.Partition.Value, original.Offset.Value);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "FATAL: Failed to publish to DLQ topic '{DlqTopic}' for inventory event at Partition {P} Offset {O}. Message permanently lost.",
                dlqTopic, original.Partition.Value, original.Offset.Value);
        }
    }

    private async Task<bool> TryConsumeWithRetries(
        string eventType, EventWrapper wrapper, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxImmediateRetries; attempt++)
        {
            try
            {
                await DispatchAsync(eventType, wrapper.Payload, ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Consume attempt {Attempt}/{Max} failed for '{EventType}'",
                    attempt + 1, MaxImmediateRetries, eventType);

                if (attempt < MaxImmediateRetries - 1)
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                    await Task.Delay(RetryDelays[attempt] + jitter, ct);
                }
            }
        }

        return false;
    }

    private async Task DispatchAsync(string eventType, JsonElement payload, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var consumers = scope.ServiceProvider.GetRequiredService<IEnumerable<IInventoryEventConsumer>>();

        var handler = consumers.FirstOrDefault(c => c.EventType == eventType);
        if (handler is null)
        {
            logger.LogDebug("No consumer registered for event type '{EventType}' — skipping", eventType);
            return;
        }

        await handler.ConsumeAsync(payload, ct);
    }

    private static string? GetHeader(ConsumeResult<string, string> result, string key)
    {
        var header = result.Message.Headers.FirstOrDefault(h => h.Key == key);
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
