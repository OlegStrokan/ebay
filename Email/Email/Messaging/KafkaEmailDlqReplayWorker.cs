using System.Text;
using Confluent.Kafka;
using Email.Options;
using Microsoft.Extensions.Options;

namespace Email.Messaging;

public sealed class KafkaEmailDlqReplayWorker(
    IOptions<KafkaOptions> kafkaOptions,
    ILogger<KafkaEmailDlqReplayWorker> logger) : BackgroundService
{
    private readonly KafkaOptions _options = kafkaOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableDlqReplay)
        {
            logger.LogInformation("DLQ replay worker disabled by configuration");
            return;
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.DlqReplayConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            IsolationLevel = IsolationLevel.ReadCommitted
        };

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            MessageSendMaxRetries = 5
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetErrorHandler((_, error) => logger.LogError("DLQ replay consumer error: {Reason}", error.Reason))
            .Build();

        using var producer = new ProducerBuilder<string, string>(producerConfig)
            .SetErrorHandler((_, error) => logger.LogError("DLQ replay producer error: {Reason}", error.Reason))
            .Build();

        consumer.Subscribe(_options.EmailDlqTopic);

        logger.LogInformation(
            "DLQ replay worker subscribed to topic {DlqTopic}. RunOnce={RunOnce}",
            _options.EmailDlqTopic,
            _options.DlqReplayRunOnce);

        if (_options.DlqReplayRunOnce)
        {
            await ReplayUntilIdleAsync(consumer, producer, stoppingToken);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult?.Message?.Value is null)
                {
                    continue;
                }

                await ReplayMessageAsync(consumeResult.Message, producer, stoppingToken);
                consumer.StoreOffset(consumeResult);
                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("DLQ replay worker stopping");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in DLQ replay loop");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ReplayUntilIdleAsync(
        IConsumer<string, string> consumer,
        IProducer<string, string> producer,
        CancellationToken cancellationToken)
    {
        var idleCount = 0;
        while (idleCount < 3 && !cancellationToken.IsCancellationRequested)
        {
            var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
            if (consumeResult?.Message?.Value is null)
            {
                idleCount++;
                continue;
            }

            idleCount = 0;
            await ReplayMessageAsync(consumeResult.Message, producer, cancellationToken);
            consumer.StoreOffset(consumeResult);
            consumer.Commit(consumeResult);
        }

        logger.LogInformation("DLQ replay run-once finished");
    }

    internal async Task ReplayMessageAsync(
        Message<string, string> message,
        IProducer<string, string> producer,
        CancellationToken cancellationToken)
    {
        var replayAttempt = GetReplayAttempt(message.Headers);
        if (replayAttempt >= _options.MaxDlqReplayAttempts)
        {
            logger.LogWarning(
                "Skipping DLQ message key {Key}. Replay attempt {Attempt} exceeded max {Max}",
                message.Key,
                replayAttempt,
                _options.MaxDlqReplayAttempts);
            return;
        }

        var delayMs = Math.Min(_options.DlqReplayBaseDelayMs * (int)Math.Pow(2, replayAttempt), 300000);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        var replayed = new Message<string, string>
        {
            Key = message.Key,
            Value = message.Value,
            Headers = new Headers
            {
                { "dlq-replay-attempt", Encoding.UTF8.GetBytes((replayAttempt + 1).ToString()) },
                { "dlq-replayed-at", Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")) }
            }
        };

        await producer.ProduceAsync(_options.EmailEventsTopic, replayed, cancellationToken);

        logger.LogInformation(
            "Replayed DLQ email message key {Key} to {Topic}. Attempt now {Attempt}",
            message.Key,
            _options.EmailEventsTopic,
            replayAttempt + 1);
    }

    private static int GetReplayAttempt(Headers headers)
    {
        var raw = headers.FirstOrDefault(h => h.Key == "dlq-replay-attempt")?.GetValueBytes();
        if (raw is null)
        {
            return 0;
        }

        return int.TryParse(Encoding.UTF8.GetString(raw), out var parsed) ? parsed : 0;
    }
}
