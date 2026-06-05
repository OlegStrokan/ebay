using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace Gateway.Api.Services;

public interface IOrderSagaEventPublisher : IDisposable
{
    Task PublishReturnShipmentDeliveredAsync(
        Guid orderId,
        string shipmentId,
        string? trackingNumber,
        DateTime deliveredAt,
        CancellationToken ct);
}

public sealed class KafkaOrderSagaEventPublisher : IOrderSagaEventPublisher
{
    private const string ReturnShipmentDeliveredEventType = "ReturnShipmentDeliveredEvent";

    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<KafkaOrderSagaEventPublisher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public KafkaOrderSagaEventPublisher(IConfiguration configuration, ILogger<KafkaOrderSagaEventPublisher> logger)
        : this(
            BuildProducer(configuration),
            configuration["Kafka:SagaTopic"] ?? "order.events",
            logger)
    {
    }

    internal KafkaOrderSagaEventPublisher(
        IProducer<string, string> producer,
        string topic,
        ILogger<KafkaOrderSagaEventPublisher> logger)
    {
        _producer = producer;
        _topic = topic;
        _logger = logger;
    }

    public async Task PublishReturnShipmentDeliveredAsync(
        Guid orderId,
        string shipmentId,
        string? trackingNumber,
        DateTime deliveredAt,
        CancellationToken ct)
    {
        var eventId = Guid.NewGuid();
        var payload = new ReturnShipmentDeliveredPayload(orderId, shipmentId, trackingNumber, deliveredAt);

        var wrapper = new EventWrapper
        {
            EventId = eventId,
            EventType = ReturnShipmentDeliveredEventType,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredOn = DateTime.UtcNow,
        };

        var message = new Message<string, string>
        {
            Key = orderId.ToString(),
            Value = JsonSerializer.Serialize(wrapper, JsonOptions),
            Headers = new Headers
            {
                { "event-type", Encoding.UTF8.GetBytes(ReturnShipmentDeliveredEventType) },
                { "event-id", Encoding.UTF8.GetBytes(eventId.ToString("D")) },
            },
        };

        await _producer.ProduceAsync(_topic, message, ct);

        _logger.LogInformation(
            "Published {EventType} for OrderId={OrderId}, ShipmentId={ShipmentId} to topic {Topic}",
            ReturnShipmentDeliveredEventType,
            orderId,
            shipmentId,
            _topic);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    private static IProducer<string, string> BuildProducer(IConfiguration configuration)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9093",
            EnableIdempotence = true,
            Acks = Acks.All,
        };

        return new ProducerBuilder<string, string>(config).Build();
    }

    private sealed class EventWrapper
    {
        public Guid EventId { get; init; }
        public string EventType { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public DateTime OccurredOn { get; init; }
    }

    private sealed record ReturnShipmentDeliveredPayload(
        Guid OrderId,
        string ShipmentId,
        string? TrackingNumber,
        DateTime DeliveredAt);
}
