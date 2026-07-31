namespace Application.Sagas.Persistence;

public interface ISagaRepository
{
    Task<SagaState?> GetByIdAsync(
        Guid sagaId,
        CancellationToken cancellationToken
    );
    
    Task<List<SagaStepLog>> GetStepLogsAsync(
        Guid sagaId,
        CancellationToken cancellationToken
    );

    Task<SagaState?> GetByCorrelationIdAsync(
        Guid correlationId,
        string sagaType, CancellationToken
            cancellationToken);

    Task SaveAsync(
        SagaState sagaState,
        CancellationToken cancellationToken
    );

    Task SaveStepAsync(
        SagaStepLog stepLog,
        CancellationToken cancellationToken
    );

    Task<List<SagaState>> GetStuckSagasAsync(
        DateTime updateBeforeCutoff,
        DateTime nowUtc,
        CancellationToken cancellationToken
    );

    Task<(List<SagaState> Items, int TotalCount)> GetSagasAsync(
        string? status,
        string? sagaType,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken
    );

    Task SaveCompensationStateAsync(
        SagaState sagaState,
        SagaStepLog stepLog,
        CancellationToken cancellationToken
    );
}
