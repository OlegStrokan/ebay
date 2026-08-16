using System.Text;
using Confluent.Kafka;
using Domain.Entities;
using Infrastructure.Callbacks;
using Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Infrastructure.Messaging;

// Publishes ledger money-events. Separate producer and topic from the order saga callbacks,
// so a slow ledger consumer never blocks saga delivery.
internal sealed class MoneyEventKafkaDispatcher : IMoneyEventDispatcher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _kafkaOptions;
    private readonly ILogger<MoneyEventKafkaDispatcher> _logger;

    public MoneyEventKafkaDispatcher(
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<MoneyEventKafkaDispatcher> logger)
    {
        _logger = logger;
        _kafkaOptions = kafkaOptions.Value;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _kafkaOptions.BootstrapServers,
            ClientId = string.IsNullOrWhiteSpace(_kafkaOptions.ProducerClientId)
                ? "payment-service"
                : $"{_kafkaOptions.ProducerClientId}-money-events",
            EnableIdempotence = true,
            MaxInFlight = 1,
            Acks = Acks.All,
            MessageSendMaxRetries = 10,
        };

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
    }

    public async Task<CallbackDeliveryResult> DispatchAsync(
        OutboundMoneyEvent moneyEvent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_kafkaOptions.BootstrapServers))
        {
            return new CallbackDeliveryResult(false, "Kafka bootstrap servers are not configured.");
        }

        if (string.IsNullOrWhiteSpace(_kafkaOptions.MoneyEventsTopic))
        {
            return new CallbackDeliveryResult(false, "Kafka money events topic is not configured.");
        }

        try
        {
            var message = new Message<string, string>
            {
                // Key on the payment so every leg of one payment stays ordered in one partition.
                Key = moneyEvent.PaymentId,
                Value = moneyEvent.PayloadJson,
                Headers = new Headers
                {
                    { "event-type", Encoding.UTF8.GetBytes(moneyEvent.EventType) },
                    { "event-id", Encoding.UTF8.GetBytes(moneyEvent.EventId) },
                },
            };

            var deliveryResult = await _producer.ProduceAsync(
                _kafkaOptions.MoneyEventsTopic,
                message,
                cancellationToken);

            _logger.LogInformation(
                "Published money event {EventId} as {EventType} to Kafka topic {Topic}. Partition={Partition}, Offset={Offset}",
                moneyEvent.EventId,
                moneyEvent.EventType,
                _kafkaOptions.MoneyEventsTopic,
                deliveryResult.Partition,
                deliveryResult.Offset);

            return new CallbackDeliveryResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish money event {EventId} to Kafka topic {Topic}",
                moneyEvent.EventId,
                _kafkaOptions.MoneyEventsTopic);

            return new CallbackDeliveryResult(false, ex.Message);
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
