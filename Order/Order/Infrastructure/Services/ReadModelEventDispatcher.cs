using Domain.Common;
using Domain.Interfaces;
using Infrastructure.Services.EventIdempotencyChecker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

// Shared event dispatch logic used by both KafkaReadModelSynchronizer (live path)
// and KafkaReadModelRetryWorker (deferred retry path).
public sealed class ReadModelEventDispatcher(
    IEventIdempotencyChecker idempotencyChecker,
    IEventStoreRepository eventStore,
    IDomainEventTypeRegistry eventTypeRegistry,
    IReadModelHandlerRegistry handlerRegistry,
    IEnumerable<IReadModelUpdater> updaters,
    ILogger<ReadModelEventDispatcher> logger) : IReadModelEventDispatcher
{
    
    public async Task<bool> DispatchAsync(
        string eventType,
        string aggregateId,
        string eventData,
        CancellationToken ct)
    {
        var eventId = ExtractEventId(eventData);

        // Fast pre-check: skip if already processed (avoids the heavier SERIALIZABLE transaction)
        if (await idempotencyChecker.HasBeenProcessedAsync(eventId, ct))
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}) already processed. Skipping duplicate.",
                eventId, eventType);
            return true;
        }

        // Atomically claim the event. Returns false if another consumer won the race.
        // By marking BEFORE applying the read-model update, we eliminate the window
        // where a crash between apply and mark causes double-application on redelivery.
        if (!await idempotencyChecker.MarkAsProcessedAsync(eventId, eventType, ct))
        {
            logger.LogInformation(
                "Event {EventId} ({EventType}) was claimed by another consumer. Skipping.",
                eventId, eventType);
            return true;
        }

        if (!eventTypeRegistry.TryGetType(eventType, out var domainEventType))
        {
            logger.LogWarning("Unknown event type {EventType}. Skipping.", eventType);
            return false;
        }
        
        var aggregateType = GetAggregateTypeFromEventType(domainEventType);
        var allEvents = await eventStore.GetEventsAsync(aggregateId, aggregateType, ct);
        var domainEvent = allEvents.FirstOrDefault(e => e.EventId == eventId);

        if (domainEvent == null)
        {
            logger.LogError(
                "Event {EventId} ({EventType}) not found in event store for aggregate {AggregateId}",
                eventId, eventType, aggregateId);
            return true;
        }

        var updater = updaters.FirstOrDefault(u => u.CanHandle(domainEvent.GetType()));
        if (updater == null)
        {
            logger.LogWarning("No updater found for event type {EventType}", domainEvent.GetType().Name);
            return true;
        }

        await handlerRegistry.HandleAsync(domainEvent, updater, ct);

        logger.LogDebug(
            "Event {EventType} dispatched to {Updater}",
            domainEvent.GetType().Name, updater.GetType().Name);

        return true;
    }

    private static Guid ExtractEventId(string eventData)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(eventData);
        if (doc.RootElement.TryGetProperty("EventId", out var eventIdElement)
            && Guid.TryParse(eventIdElement.GetString(), out var id))
            return id;

        throw new InvalidOperationException("Message is missing a valid 'EventId' property");
    }

    private static string GetAggregateTypeFromEventType(Type domainEventType)
    {
        var name = domainEventType.Name;
        var ns = domainEventType.Namespace ?? string.Empty;
        if (name.StartsWith("Return") || ns.Contains("Return"))
            return AggregateTypes.ReturnRequest;
        if (name.StartsWith("B2BOrder") || ns.Contains("B2BOrder"))
            return AggregateTypes.B2BOrder;
        if (name.StartsWith("RecurringOrder") || ns.Contains("RecurringOrder"))
            return AggregateTypes.RecurringOrder;
        return AggregateTypes.Order;
    }
}
