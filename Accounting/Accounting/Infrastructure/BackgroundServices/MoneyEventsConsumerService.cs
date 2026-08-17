using System.Text;
using Application.Commands.IngestMoneyEvent;
using Confluent.Kafka;
using Infrastructure.Messaging;
using Infrastructure.Options;
using MediatR;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

public sealed class MoneyEventsConsumerService : BackgroundService
{
    private const string EventTypeHeader = "event-type";
    private const string EventIdHeader = "event-id";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoneyEventsConsumerService> _logger;
    private readonly MoneyEventConsumerOptions _consumerOptions;
    private readonly string _topic;
    private readonly ConsumerConfig _consumerConfig;

    private int _failuresAtCurrentOffset;

    public MoneyEventsConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> kafkaOptions,
        IOptions<MoneyEventConsumerOptions> consumerOptions,
        ILogger<MoneyEventsConsumerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _consumerOptions = consumerOptions.Value;

        var kafka = kafkaOptions.Value;
        _topic = kafka.MoneyEventsTopic;

        _consumerConfig = new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = _consumerOptions.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            IsolationLevel = IsolationLevel.ReadCommitted,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_consumerOptions.Enabled)
        {
            _logger.LogWarning(
                "MoneyEventsConsumerService is disabled. No money events will reach the ledger while it stays off.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_consumerConfig.BootstrapServers) || string.IsNullOrWhiteSpace(_topic))
        {
            _logger.LogError(
                "MoneyEventsConsumerService cannot start: Kafka BootstrapServers or MoneyEventsTopic is not configured.");
            return;
        }

        await Task.Yield();

        using var consumer = new ConsumerBuilder<string, string>(_consumerConfig)
            .SetErrorHandler((_, error) =>
                _logger.LogError("Kafka money event consumer error: {Reason}", error.Reason))
            .Build();

        consumer.Subscribe(_topic);

        _logger.LogInformation(
            "MoneyEventsConsumerService started. Topic: {Topic}, GroupId: {GroupId}",
            _topic,
            _consumerOptions.ConsumerGroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(stoppingToken);

                    if (result?.Message is null)
                        continue;

                    if (await TryHandleAsync(result, stoppingToken))
                    {
                        _failuresAtCurrentOffset = 0;
                        consumer.Commit(result);
                        continue;
                    }

                    // Transient failure. Seek back so the event is genuinely redelivered: not
                    // committing is not enough on its own, because the consumer's position has
                    // already moved past it and it would otherwise only come round again after a
                    // restart or a rebalance.
                    _failuresAtCurrentOffset++;

                    if (_failuresAtCurrentOffset < NormalizePositive(_consumerOptions.MaxOffsetRetries, 5))
                    {
                        consumer.Seek(result.TopicPartitionOffset);
                        await Task.Delay(
                            TimeSpan.FromSeconds(NormalizePositive(_consumerOptions.RetryDelaySeconds, 5)),
                            stoppingToken);
                        continue;
                    }

                    // Holding the partition forever would stall every later money event for every
                    // payment, so the offset moves on. The ledger is now knowingly short one
                    // posting: that is exactly the aggregate drift ReconcileLedgerWorker exists to
                    // catch, and the event is still in Kafka to be replayed once the cause is fixed.
                    _logger.LogCritical(
                        "Giving up on money event {EventId} at {Topic}-{Partition} offset {Offset} after {Attempts} attempts. " +
                        "Committing past it to keep the partition moving. THE LEDGER IS NOW MISSING THIS POSTING and will show drift until the event is replayed.",
                        GetHeader(result, EventIdHeader) ?? "unknown",
                        result.Topic,
                        result.Partition.Value,
                        result.Offset.Value,
                        _failuresAtCurrentOffset);

                    _failuresAtCurrentOffset = 0;
                    consumer.Commit(result);
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
                    _logger.LogError(ex, "Unexpected error in the money event consumer loop on {Topic}", _topic);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MoneyEventsConsumerService cancellation requested.");
        }
        finally
        {
            consumer.Unsubscribe();
            consumer.Close();
            _logger.LogInformation("MoneyEventsConsumerService stopped");
        }
    }

    // True means the offset may be committed - either the event was handled or it is poison and
    // retrying it would never help. False means try again.
    private async Task<bool> TryHandleAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        if (!MoneyEventPayloadParser.TryParse(result.Message.Value, out var payload, out var parseError))
        {
            // Unparseable is permanent. Log loudly and move on rather than block the partition.
            _logger.LogError(
                "Discarding unparseable message at {Topic}-{Partition} offset {Offset} (event-type header {EventType}): {Error}",
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                GetHeader(result, EventTypeHeader) ?? "absent",
                parseError);

            return true;
        }

        var headerEventType = GetHeader(result, EventTypeHeader);

        if (headerEventType is not null && headerEventType != payload!.EventType)
            _logger.LogWarning(
                "Money event {EventId} has event-type header {HeaderEventType} but payload eventType {PayloadEventType}. Using the payload.",
                payload.EventId,
                headerEventType,
                payload.EventType);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var ingestion = await mediator.Send(new IngestMoneyEventCommand(payload!), cancellationToken);

            if (ingestion.IsSuccess)
                return true;

            // A rejected payload is deterministic - the same message will be rejected the same way
            // for ever - so this is poison, not a transient fault.
            _logger.LogError(
                "Money event {EventId} at {Topic}-{Partition} offset {Offset} was rejected and will not be retried: {Error}",
                payload!.EventId,
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                ingestion.Error);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Anything thrown here is infrastructure - the database is down, the connection
            // dropped - so it is worth retrying the same offset.
            _logger.LogError(
                ex,
                "Failed to ingest money event {EventId} of type {EventType} at {Topic}-{Partition} offset {Offset}. Will retry.",
                payload!.EventId,
                payload.EventType,
                result.Topic,
                result.Partition.Value,
                result.Offset.Value);

            return false;
        }
    }

    private static string? GetHeader(ConsumeResult<string, string> result, string key)
    {
        var header = result.Message.Headers?.FirstOrDefault(h => h.Key == key);
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    private static int NormalizePositive(int value, int fallback) => value > 0 ? value : fallback;
}
