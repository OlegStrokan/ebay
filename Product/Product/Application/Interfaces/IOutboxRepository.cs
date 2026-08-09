using Application.Models;

namespace Application.Interfaces;

public interface IOutboxRepository
{
    Task AddAsync(Guid messageId, string type, string content, DateTime occurredOn,
                  string aggregateId, CancellationToken ct = default);
    Task<List<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize, int maxRetries, CancellationToken ct = default);
    Task CommitClaimAsync(CancellationToken ct = default);
    // for integration/e2e tests
    Task<int> MarkRetryExhaustedMessagesAsProcessedAsync(int batchSize, int maxRetries, CancellationToken ct = default);
    Task MarkAsProcessedAsync(Guid messageId, CancellationToken ct = default);
    Task IncrementRetryCountAsync(Guid messageId, string error, CancellationToken ct = default);
    Task DeleteAsync(Guid messageId, CancellationToken ct = default);
    Task DeleteProcessedMessagesAsync(CancellationToken ct = default);
}
