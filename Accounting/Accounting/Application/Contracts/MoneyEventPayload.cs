namespace Application.Contracts;

public sealed record MoneyEventPayload(
    string EventId,
    string EventType,
    string PaymentId,
    string OrderId,
    string? RefundId,
    string? ProviderPaymentIntentId,
    decimal Amount,
    string Currency,
    decimal Fee,
    decimal Tax,
    DateTime OccurredAt);
