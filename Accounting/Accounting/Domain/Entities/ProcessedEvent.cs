namespace Domain.Entities;

// consumer-side idempotency guard
public sealed class ProcessedEvent
{
    public string EventId { get; private set; } = null!;

    public string EventType { get; private set; } = null!;

    public DateTime ProcessedAt { get; private set; }

    private ProcessedEvent()
    {
    }

    public static ProcessedEvent Create(string eventId, string eventType, DateTime processedAt)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            throw new ArgumentException("EventId is required.", nameof(eventId));

        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("EventType is required.", nameof(eventType));

        return new ProcessedEvent
        {
            EventId = eventId.Trim(),
            EventType = eventType.Trim(),
            ProcessedAt = processedAt,
        };
    }
}
