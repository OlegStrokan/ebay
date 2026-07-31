namespace Application.DTOs;

public sealed record InventoryExpiredEventDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string OrderId { get; init; } = string.Empty;
    public DateTime? OccurredAtUtc { get; init; }
}
