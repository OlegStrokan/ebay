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

    Task SaveAsync(FailedCompensationRetry retry, CancellationToken cancellationToken);
}
