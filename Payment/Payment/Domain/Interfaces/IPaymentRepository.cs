using Domain.Entities;
using Domain.ValueObjects;

namespace Domain.Interfaces;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(PaymentId paymentId, CancellationToken cancellationToken = default);

    Task<Payment?> GetByProviderPaymentIntentIdAsync(
        ProviderPaymentIntentId providerPaymentIntentId,
        CancellationToken cancellationToken = default);

    Task<Payment?> GetByOrderIdAndIdempotencyKeyAsync(
        string orderId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Payment>> GetAllByOrderIdAsync(
        string orderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Payment>> GetPendingProviderConfirmationsOlderThanAsync(
        DateTime threshold,
        int maxCount,
        CancellationToken cancellationToken = default);
 
    Task<IReadOnlyList<Payment>> GetAuthorizedOlderThanAsync(
        DateTime threshold,
        int maxCount,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<PaymentId, Payment>> GetByIdsAsync(
        IReadOnlyCollection<PaymentId> paymentIds,
        CancellationToken cancellationToken = default);

    Task AddAsync(Payment payment, CancellationToken cancellationToken = default);

    Task UpdateAsync(Payment payment, CancellationToken cancellationToken = default);
}