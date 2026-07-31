using System.Text;
using Application.Interfaces;
using Confluent.Kafka;
using Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

public sealed class InventoryEventsConsumerService : BackgroundService
{
    private const string ConsumerGroupId = "order-service-inventory";
    private const string EventTypeHeader = "event-type";
    private const int MaxOffsetRetries = 3;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InventoryEventsConsumerService> _logger;
    private readonly IConsumer<string, string> _consumer;
    private readonly string _topic;

    private int _failuresAtCurrentOffset;

    public InventoryEventsConsumerService(
        IServiceProvider serviceProvider,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<InventoryEventsConsumerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        var options = kafkaOptions.Value;
        _topic = options.InventoryEventsTopic;

        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            IsolationLevel = IsolationLevel.ReadCommitted,
        };

        _consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
                logger.LogError("Kafka inventory consumer error: {Reason}", error.Reason))
            .Build();
    }

    // Testability constructor - lets unit tests supply a pre-built consumer.
    public InventoryEventsConsumerService(
        IServiceProvider serviceProvider,
        ILogger<InventoryEventsConsumerService> logger,
        IConsumer<string, string> consumer,
        IOptions<KafkaOptions> kafkaOptions)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _consumer = consumer;
        _topic = kafkaOptions.Value.InventoryEventsTopic;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _consumer.Subscribe(_topic);
        _logger.LogInformation(
            "InventoryEventsConsumerService started. Topic: {Topic}, GroupId: {GroupId}",
            _topic, ConsumerGroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = _consumer.Consume(stoppingToken);

                    if (consumeResult?.Message?.Value is null)
                        continue;

                    var eventType = GetHeader(consumeResult, EventTypeHeader);

                    if (string.IsNullOrEmpty(eventType))
                    {
                        _logger.LogWarning(
                            "Inventory event at partition {Partition} offset {Offset} has no " +
                            "'{Header}' header. Skipping.",
                            consumeResult.Partition, consumeResult.Offset, EventTypeHeader);

                        _consumer.Commit(consumeResult);
                        continue;
                    }

                    try
                    {
                        await ProcessEventAsync(eventType, consumeResult.Message.Value, stoppingToken);
                        _failuresAtCurrentOffset = 0;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Seek back so the event is genuinely retried. Not committing is not enough
                        // on its own: the consumer's position has already advanced past it, so
                        // without the seek it would only be reprocessed after a restart or rebalance
                        _failuresAtCurrentOffset++;

                        if (_failuresAtCurrentOffset < MaxOffsetRetries)
                        {
                            _logger.LogError(
                                ex,
                                "Failed to process inventory event {EventType} at offset {Offset} " +
                                "(attempt {Attempt}/{Max}). Seeking back to retry.",
                                eventType, consumeResult.Offset, _failuresAtCurrentOffset, MaxOffsetRetries);

                            _consumer.Seek(consumeResult.TopicPartitionOffset);
                            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                            continue;
                        }

                        // Committing past a poison message is the lesser evil: holding the partition
                        // forever would also block every later InventoryExpired. The saga is not
                        // left unprotected - SagaWatchdogService still reaps it on its wait deadline
                        _logger.LogCritical(
                            ex,
                            "Giving up on inventory event {EventType} at offset {Offset} after {Max} " +
                            "attempts. Committing past it to keep the partition moving; the affected " +
                            "saga will be reaped by its wait deadline instead.",
                            eventType, consumeResult.Offset, MaxOffsetRetries);

                        _failuresAtCurrentOffset = 0;
                    }

                    _consumer.Commit(consumeResult);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error on {Topic}", _topic);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing inventory event from {Topic}", _topic);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InventoryEventsConsumerService cancellation requested.");
        }
        finally
        {
            _consumer.Unsubscribe();
            _consumer.Close();
            _logger.LogInformation("InventoryEventsConsumerService stopped");
        }
    }

    private async Task ProcessEventAsync(string eventType, string payload, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<ISagaHandlerFactory>();
        var handler = factory.GetHandler(scope.ServiceProvider, eventType);

        if (handler is null)
        {
            // Most inventory events (Reserved, Confirmed, Released) are of no interest to Order.
            _logger.LogDebug("No handler for inventory event type {EventType}. Skipping.", eventType);
            return;
        }

        _logger.LogInformation(
            "Processing inventory event {EventType} with {HandlerType}",
            eventType, handler.GetType().Name);

        await handler.HandleAsync(payload, cancellationToken);
    }

    private static string? GetHeader(ConsumeResult<string, string> result, string key)
    {
        var header = result.Message.Headers?.FirstOrDefault(h => h.Key == key);
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    public override void Dispose()
    {
        _consumer?.Dispose();
        base.Dispose();
    }
}
