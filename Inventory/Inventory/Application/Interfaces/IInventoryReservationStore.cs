using Application.Models;

namespace Application.Interfaces;

public interface IInventoryReservationStore
{
    Task<ReserveInventoryResult> ReserveAsync(
        Guid orderId,
        IReadOnlyCollection<ReserveStockItem> items,
        CancellationToken cancellationToken);

    Task<ReleaseInventoryResult> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken);

    Task<ReleaseInventoryResult> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken);

    Task<int> ExpireStaleReservationsAsync(
        DateTime olderThanUtc,
        int batchSize,
        CancellationToken cancellationToken);

    // Admin/ops console lookup: the reservation for an order, if any.
    Task<ReservationSummary?> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken cancellationToken);
}

public sealed record ReservationSummary(
    Guid ReservationId,
    Guid OrderId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ReservationItemSummary> Items);

public sealed record ReservationItemSummary(Guid ProductId, int Quantity);
