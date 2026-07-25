using Application.Models;

namespace Application.Interfaces;

public interface IFailedCompensationRetryRepository
{
   Task EnqueueIfNotExistsAsync(
        Guid sagaId,
        string sagaType,
        string lastFailedStep,
        string lastError,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FailedCompensationRetry>> ClaimDuePendingAsync(
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken);

    // Admin/ops console lookup: the most recent retry row for a saga, regardless of status
    // (including Exhausted/Completed), so an operator-triggered retry can revive it.
    Task<FailedCompensationRetry?> GetBySagaIdAsync(
        Guid sagaId,
        CancellationToken cancellationToken);

    Task SaveAsync(FailedCompensationRetry retry, CancellationToken cancellationToken);
}
