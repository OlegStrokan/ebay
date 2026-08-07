using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.DTOs;
using Application.Interfaces;
using Confluent.Kafka;
using Domain.Common;
using Domain.Events.CreateOrder;
using Domain.Events.OrderReturn;
using Microsoft.Extensions.Options;

namespace Infrastructure.Messaging;

// kafka has no auto-instrumentation build for opentelemetry so we traced it manually
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private static readonly ActivitySource _activitySource = new("OrderService.Kafka");
    private const string OrderConfirmationEmailRequestedEventType = "OrderConfirmationEmailRequested";

    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;
    private readonly KafkaOptions _options;

    public KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        _options = options.Value;

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            EnableIdempotence = true,
            MaxInFlight = 1,
            Acks = Acks.All,
            MessageSendMaxRetries = 10
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    // constructor for testing. supply a pre-built producer and optionally override topic config.
    public KafkaEventPublisher(
        IProducer<string, string> producer,
        ILogger<KafkaEventPublisher> logger,
        KafkaOptions? options = null)
    {
        _producer = producer;
        _logger = logger;
        _options = options ?? new KafkaOptions();
    }
    

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken)
        where TEvent : IDomainEvent
    {
        try
        {
            var eventType = @event.GetType().Name;

            var eventWrapper = new EventWrapper
            {
                EventId = @event.EventId,
                EventType = eventType,
                Payload = SerializeEvent(@event),
                OccurredOn = @event.OccurredOn
            };

            using var activity = _activitySource.StartActivity("kafka.publish", ActivityKind.Producer);

            var headers = new Headers
            {
                { "event-type", Encoding.UTF8.GetBytes(eventType) },
                { "event-id", Encoding.UTF8.GetBytes(@event.EventId.ToString()) }
            };

            if (Activity.Current != null)
                headers.Add("traceparent",
                    Encoding.UTF8.GetBytes(
                        $"00-{Activity.Current.TraceId}-{Activity.Current.SpanId}-01"));

            var message = new Message<string, string>
            {
                Key = GetEventKey(@event),
                Value = JsonSerializer.Serialize(eventWrapper),
                Headers = headers
            };

            var result = await _producer.ProduceAsync(_options.OrderEventsTopic, message, cancellationToken);

            _logger.LogInformation(
                "Published {EventType} to Kafka Partition: {Partition}, Offset: {Offset}",
                eventType,
                result.Partition,
                result.Offset);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event {EventType}", @event.GetType().Name);
            throw;
        }
}

    public async Task PublishRawAsync(Guid eventId, string typeName, string content, DateTime occuredOn, string aggregateId, CancellationToken cancellationToken)
    {
        try
        {

            var eventWrapper = new EventWrapper
            {
                EventId = eventId,
                EventType = typeName,
                Payload = content,
                OccurredOn = occuredOn
            };

            var finalJson = JsonSerializer.Serialize(eventWrapper);

            using var activity = _activitySource.StartActivity("kafka.publish", ActivityKind.Producer);

            var headers = new Headers
            {
                { "event-type", Encoding.UTF8.GetBytes(typeName) },
                { "event-id", Encoding.UTF8.GetBytes(eventId.ToString()) }
            };

            if (Activity.Current != null)
                headers.Add("traceparent",
                    Encoding.UTF8.GetBytes(
                        $"00-{Activity.Current.TraceId}-{Activity.Current.SpanId}-01"));

            var message = new Message<string, string>
            {
                Key = aggregateId,
                Value = finalJson,
                Headers = headers
            };

            var targetTopic = ResolveTopicForRawEvent(typeName);
            var result = await _producer.ProduceAsync(targetTopic, message, cancellationToken);

            _logger.LogInformation(
                "Published {EventType} to Kafka Topic: {Topic}, Partition: {Partition}, Offset: {Offset}",
                typeName,
                targetTopic,
                result.Partition,
                result.Offset);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event {EventType}", typeName);
            throw;
        }
    }


    public string Serialize(IDomainEvent @event) => SerializeEvent(@event);

    private string ResolveTopicForRawEvent(string typeName)
    {
        return typeName == OrderConfirmationEmailRequestedEventType
            ? _options.EmailEventsTopic
            : _options.OrderEventsTopic;
    }

    private string SerializeEvent<TEvent>(TEvent @event) where TEvent : IDomainEvent
    {
        return @event switch
        {
            OrderCreatedEvent e => System.Text.Json.JsonSerializer.Serialize(new OrderCreatedEventDto
            {
                OrderId = e.OrderId.Value,
                CustomerId = e.CustomerId.Value,
                TotalAmount = e.TotalPrice.Amount,
                Currency = e.TotalPrice.Currency,
                DeliveryAddress = new AddressDto(
                    e.DeliveryAddress.Street,
                    e.DeliveryAddress.City,
                    e.DeliveryAddress.Country,
                    e.DeliveryAddress.PostalCode
                ),
                Items = e.Items.Select(i => new OrderItemDto(
                    i.ProductId.Value,
                    i.Quantity,
                    i.PriceAtPurchase.Amount,
                    i.PriceAtPurchase.Currency
                )).ToList(),
                PaymentIntentId = e.ProviderPaymentIntentId,
                PaymentMethod = Enum.TryParse<Application.Common.Enums.PaymentMethod>(e.PaymentMethod, ignoreCase: true, out var pm)
                    ? pm
                    : throw new InvalidOperationException($"Unknown PaymentMethod '{e.PaymentMethod}' for order {e.OrderId.Value}"),
                CreatedAt = e.CreatedAt
            }),

            ReturnRequestCreatedEvent e => System.Text.Json.JsonSerializer.Serialize(new ReturnRequestCreatedEventDto
            {
                ReturnRequestId = e.ReturnRequestId.Value,
                OrderId = e.OrderId.Value,
                CustomerId = e.CustomerId.Value,
                Reason = e.Reason,
                ItemsToReturn = e.ItemsToReturn.Select(i => new OrderItemDto(
                    i.ProductId.Value,
                    i.Quantity,
                    i.PriceAtPurchase.Amount,
                    i.PriceAtPurchase.Currency
                )).ToList(),
                RefundAmount = e.RefundAmount.Amount,
                Currency = e.RefundAmount.Currency,
                RequestedAt = e.RequestedAt
            }),

            _ => System.Text.Json.JsonSerializer.Serialize(@event, @event.GetType())
        };
    }


    private string GetEventKey<TEvent>(TEvent @event) where TEvent : IDomainEvent
    {
        return @event switch
        {
            OrderCreatedEvent e => e.OrderId.Value.ToString(),
            OrderPaidEvent e => e.OrderId.Value.ToString(),
            OrderApprovedEvent e => e.OrderId.Value.ToString(),
            OrderCompletedEvent e => e.OrderId.Value.ToString(),
            OrderCancelledEvent e => e.OrderId.Value.ToString(),
            OrderTrackingAssignedEvent e => e.OrderId.Value.ToString(),
            ReturnRequestCreatedEvent e => e.OrderId.Value.ToString(),
            ReturnItemsReceivedEvent e => e.OrderId.Value.ToString(),
            ReturnRefundProcessedEvent e => e.OrderId.Value.ToString(),
            ReturnCompletedEvent e => e.OrderId.Value.ToString(),
            _ => @event.EventId.ToString()
        };
    }

    public void Dispose()
    {
        _producer?.Flush();
        _producer?.Dispose();
    }
}