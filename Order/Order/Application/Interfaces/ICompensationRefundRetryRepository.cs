using Application.Models;

namespace Application.Interfaces;

public interface ICompensationRefundRetryRepository
{
    Task<CompensationRefundRetry> EnqueueIfNotExistsAsync(
        Guid orderId,
        string paymentId,
        decimal amount,
        string currency,
        string reason,
        CancellationToken cancellationToken);

    Task<CompensationRefundRetry?> GetPendingByOrderAndPaymentAsync(
        Guid orderId,
        string paymentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompensationRefundRetry>> GetDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompensationRefundRetry>> ClaimDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken);

    Task SaveAsync(CompensationRefundRetry retry, CancellationToken cancellationToken);
}
