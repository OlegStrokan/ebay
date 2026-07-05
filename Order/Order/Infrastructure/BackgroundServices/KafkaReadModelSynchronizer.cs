using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.Interfaces;
using Application.Models;
using Confluent.Kafka;
using Infrastructure.Messaging;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

// kafka has no auto-instrumentation built for opentelemetry so we trace it manually
public sealed class KafkaReadModelSynchronizer : BackgroundService
{
    private static readonly ActivitySource _activitySource = new("OrderService.Kafka");

    private const int MaxImmediateRetries = 3;
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<KafkaReadModelSynchronizer> _logger;
    private readonly IConsumer<string, string> _consumer;
    private readonly List<string> _topics;
    private readonly TimeSpan[] _retryDelays;
    private readonly HashSet<TopicPartition> _pausedPartitions = new();

    public KafkaReadModelSynchronizer(
        IServiceProvider serviceProvider,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<KafkaReadModelSynchronizer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        var opts = kafkaOptions.Value;

        var kafkaConfig = new ConsumerConfig
        {
            BootstrapServers = opts.BootstrapServers,
            GroupId = "read-model-updater",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            IsolationLevel = IsolationLevel.ReadCommitted
        };

        _consumer = new ConsumerBuilder<string, string>(kafkaConfig)
            .SetErrorHandler((_e, error) => logger.LogError("Kafka consumer error: {Error}", error.Reason))
            .Build();

        _topics = [opts.OrderEventsTopic, opts.ReturnEventsTopic];
        _retryDelays = DefaultRetryDelays;
    }

    // Testability constructor - injects a pre-built consumer so unit tests can substitute it.
    public KafkaReadModelSynchronizer(
        IServiceProvider serviceProvider,
        ILogger<KafkaReadModelSynchronizer> logger,
        IConsumer<string, string> consumer,
        IOptions<KafkaOptions> kafkaOptions,
        TimeSpan[]? retryDelays = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _consumer = consumer;
        var opts = kafkaOptions.Value;
        _topics = [opts.OrderEventsTopic, opts.ReturnEventsTopic];
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _consumer.Subscribe(_topics);

        _logger.LogInformation("Kafka read model synchronizer started. Subscribed to topics: {Topics}",
            string.Join(", ", _topics));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TryResumePausedPartitionsAsync(stoppingToken);

                try
                {
                    // Timeout-based poll so TryResumePausedPartitions gets a heartbeat
                    // even when the topic is quiet. Never blocks shutdown indefinitely.
                    var consumeResult = _consumer.Consume(TimeSpan.FromSeconds(1));
                    if (consumeResult is null) continue;

                    var eventType = GetHeader(consumeResult, "event-type");
                    if (string.IsNullOrEmpty(eventType))
                    {
                        _logger.LogWarning(
                            "Message at Partition {P} Offset {O} has no event-type header — skipping",
                            consumeResult.Partition.Value, consumeResult.Offset.Value);
                        _consumer.StoreOffset(consumeResult);
                        _consumer.Commit(consumeResult);
                        continue;
                    }

                    ActivityContext parentCtx = default;
                    var traceparent = GetHeader(consumeResult, "traceparent");
                    if (traceparent is not null)
                        ActivityContext.TryParse(traceparent, null, out parentCtx);

                    using var activity = _activitySource.StartActivity(
                        $"kafka.consume.read-model.{eventType}", ActivityKind.Consumer, parentCtx);
                    activity?.SetTag("messaging.system", "kafka");
                    activity?.SetTag("messaging.kafka.consumer.group", "read-model-updater");

                    if (!TryParseEventId(consumeResult.Message.Value, out _))
                    {
                        _logger.LogError(
                            "Message at Partition {P} Offset {O} (type={EventType}) has no parseable EventId — dead-lettering immediately without retry.",
                            consumeResult.Partition.Value, consumeResult.Offset.Value, eventType);
                        await DeadLetterMalformedEventAsync(consumeResult, eventType, stoppingToken);
                        _consumer.StoreOffset(consumeResult);
                        _consumer.Commit(consumeResult);
                        continue;
                    }

                    var (success, lastEx) = await TryProcessWithRetriesAsync(consumeResult, stoppingToken);

                    if (success)
                    {
                        _consumer.StoreOffset(consumeResult);
                        _consumer.Commit(consumeResult);
                    }
                    else
                    {
                        await HandleExhaustedRetriesAsync(consumeResult, eventType, lastEx!, stoppingToken);
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in read model synchronizer outer loop");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
        finally
        {
            _consumer.Close();
            _logger.LogInformation("Kafka read model synchronizer stopped");
        }
    }
    
    private async Task<(bool Success, Exception? LastException)> TryProcessWithRetriesAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken ct)
    {
        Exception? lastEx = null;

        for (var attempt = 0; attempt < MaxImmediateRetries; attempt++)
        {
            try
            {
                await DispatchAsync(consumeResult, ct);
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning(
                    ex,
                    "Processing attempt {Attempt}/{Max} failed for Partition {P} Offset {O}",
                    attempt + 1, MaxImmediateRetries,
                    consumeResult.Partition.Value, consumeResult.Offset.Value);

                if (attempt < MaxImmediateRetries - 1)
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                    await Task.Delay(_retryDelays[attempt] + jitter, ct);
                }
            }
        }

        return (false, lastEx);
    }

    private async Task DispatchAsync(ConsumeResult<string, string> consumeResult, CancellationToken ct)
    {
        var eventType = GetHeader(consumeResult, "event-type")!;
        var aggregateId = consumeResult.Message.Key ?? string.Empty;
        var eventData = consumeResult.Message.Value;

        _logger.LogDebug(
            "Processing event {EventType} from topic {Topic} at offset {Offset}",
            eventType, consumeResult.Topic, consumeResult.Offset);

        using var scope = _serviceProvider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IReadModelEventDispatcher>();
        await dispatcher.DispatchAsync(eventType, aggregateId, eventData, ct);

        _logger.LogInformation(
            "Successfully processed event {EventType} at offset {Offset}",
            eventType, consumeResult.Offset);
    }

    private async Task HandleExhaustedRetriesAsync(
        ConsumeResult<string, string> consumeResult,
        string eventType,
        Exception ex,
        CancellationToken ct)
    {
        var kind = ReadModelFailureClassifier.Classify(ex);

        switch (kind)
        {
            case ReadModelFailureKind.Systemic:
                _logger.LogError(
                    ex,
                    "Systemic failure (DB/network) for {EventType} at Partition {P} Offset {O}. " +
                    "Pausing partition until DB recovers.",
                    eventType, consumeResult.Partition.Value, consumeResult.Offset.Value);

                _consumer.Seek(new TopicPartitionOffset(consumeResult.TopicPartition, consumeResult.Offset));
                PausePartition(consumeResult.TopicPartition);
                break;

            case ReadModelFailureKind.MessageSpecific:
                _logger.LogWarning(
                    ex,
                    "Message-specific failure for {EventType} at Partition {P} Offset {O}. " +
                    "Moving to durable retry store.",
                    eventType, consumeResult.Partition.Value, consumeResult.Offset.Value);

                var persisted = await TryPersistRetryRecordAsync(consumeResult, eventType, ex, ct);

                if (persisted)
                {
                    _consumer.StoreOffset(consumeResult);
                    _consumer.Commit(consumeResult);
                }
                else
                {
                    _logger.LogError(
                        "Failed to persist retry record for {EventType} at Partition {P} Offset {O}. " +
                        "NOT committing Kafka offset.",
                        eventType, consumeResult.Partition.Value, consumeResult.Offset.Value);

                    _consumer.Seek(new TopicPartitionOffset(consumeResult.TopicPartition, consumeResult.Offset));
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                break;
        }
    }

    private async Task<bool> TryPersistRetryRecordAsync(
        ConsumeResult<string, string> consumeResult,
        string eventType,
        Exception ex,
        CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var retryRepo = scope.ServiceProvider.GetRequiredService<IKafkaRetryRepository>();

            var record = KafkaRetryRecord.Create(
                eventId: TryExtractEventId(consumeResult.Message.Value),
                eventType: eventType,
                topic: consumeResult.Topic,
                partition: consumeResult.Partition.Value,
                offset: consumeResult.Offset.Value,
                messageKey: consumeResult.Message.Key,
                payload: consumeResult.Message.Value,
                headers: SerializeHeaders(consumeResult.Message.Headers),
                correlationId: GetHeader(consumeResult, "traceparent"),
                errorMessage: Truncate(ex.Message, 2000),
                errorType: ex.GetType().FullName,
                nextRetryAt: DateTime.UtcNow.AddMinutes(3));

            await retryRepo.PersistAsync(record, ct);
            return true;
        }
        catch (Exception persistEx)
        {
            _logger.LogError(persistEx, "Retry store persistence failed for event {EventType}", eventType);
            return false;
        }
    }

    private void PausePartition(TopicPartition tp)
    {
        if (_pausedPartitions.Add(tp))
        {
            _consumer.Pause([tp]);
            _logger.LogWarning(
                "Paused partition {Topic}-{Partition} due to systemic DB/network failure",
                tp.Topic, tp.Partition.Value);
        }
    }

    private async Task TryResumePausedPartitionsAsync(CancellationToken ct)
    {
        if (_pausedPartitions.Count == 0) return;

        var healthy = await IsDbHealthyAsync(ct);
        if (!healthy) return;

        foreach (var tp in _pausedPartitions)
        {
            _consumer.Resume([tp]);
            _logger.LogInformation(
                "Resumed partition {Topic}-{Partition} — DB is healthy again",
                tp.Topic, tp.Partition.Value);
        }

        _pausedPartitions.Clear();
    }

    private async Task<bool> IsDbHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            return false;
        }
    }
    
    private static string? GetHeader(ConsumeResult<string, string> result, string key)
    {
        var header = result.Message.Headers.FirstOrDefault(h => h.Key == key);
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    private Guid? TryExtractEventId(string? eventData)
    {
        if (TryParseEventId(eventData, out var id)) return id;
        return null;
    }

   private static bool TryParseEventId(string? payload, out Guid eventId)
    {
        eventId = default;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("EventId", out var el))
                return Guid.TryParse(el.GetString(), out eventId);
        }
        catch (JsonException) { }
        return false;
    }

    private async Task DeadLetterMalformedEventAsync(
        ConsumeResult<string, string> consumeResult,
        string eventType,
        CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dlq = scope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
            await dlq.AddAsync(
                messageId: Guid.NewGuid(),
                type: "MalformedEventId",
                content: consumeResult.Message.Value ?? string.Empty,
                occuredOn: DateTime.UtcNow,
                failureReason: $"Message type '{eventType}' at Partition {consumeResult.Partition.Value} Offset {consumeResult.Offset.Value} is missing a parseable EventId — cannot guarantee idempotency.",
                retryCount: 0,
                aggregateId: consumeResult.Message.Key ?? string.Empty,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to write malformed-EventId dead-letter record for Partition {P} Offset {O}",
                consumeResult.Partition.Value, consumeResult.Offset.Value);
        }
    }

    private static string? SerializeHeaders(Headers? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        var dict = new Dictionary<string, string>();
        foreach (var h in headers)
            dict[h.Key] = Encoding.UTF8.GetString(h.GetValueBytes());
        return JsonSerializer.Serialize(dict);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null
        : value.Length <= maxLength ? value
        : value[..maxLength];

    public override void Dispose()
    {
        _consumer?.Dispose();
        base.Dispose();
    }
}