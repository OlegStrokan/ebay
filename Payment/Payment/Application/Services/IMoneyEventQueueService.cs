using Application.DTOs;
using Domain.Entities;

namespace Application.Services;

// Queues ledger money-events into the outbox. Callers must run inside the same
// UnitOfWork.SaveChangesAsync as the money mutation itself.
public interface IMoneyEventQueueService
{
    Task<MoneyEventQueuedDto> QueuePaymentAuthorizedAsync(
        Payment payment,
        CancellationToken cancellationToken = default);

    Task<MoneyEventQueuedDto> QueuePaymentVoidedAsync(
        Payment payment,
        CancellationToken cancellationToken = default);

    Task<MoneyEventQueuedDto> QueuePaymentCapturedAsync(
        Payment payment,
        CancellationToken cancellationToken = default);

    Task<MoneyEventQueuedDto> QueueRefundIssuedAsync(
        Payment payment,
        Refund refund,
        CancellationToken cancellationToken = default);
}
