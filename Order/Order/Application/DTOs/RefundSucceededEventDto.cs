namespace Application.DTOs;

public sealed record RefundSucceededEventDto
{
    public string OrderId { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public string? RefundId { get; init; }
    public string? ProviderRefundId { get; init; }
    public string? CallbackEventId { get; init; }
    public DateTime? OccurredOn { get; init; }
}
