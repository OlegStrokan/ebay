namespace Application.DTOs;

public sealed record MoneyEventQueuedDto(
    string EventId,
    string EventType,
    string PaymentId,
    string OrderId,
    DateTime QueuedAt);
