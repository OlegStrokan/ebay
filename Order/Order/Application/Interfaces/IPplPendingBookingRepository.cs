using Application.Models;

namespace Application.Interfaces;

public interface IPplPendingBookingRepository
{
    Task<PplPendingBooking> EnqueueAsync(Guid orderId, string referenceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PplPendingBooking>> ClaimDuePendingAsync(DateTime nowUtc, int batchSize, CancellationToken cancellationToken);
    Task SaveAsync(PplPendingBooking booking, CancellationToken cancellationToken);
}
