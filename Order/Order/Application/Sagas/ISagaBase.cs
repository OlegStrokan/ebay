namespace Application.Sagas;

// Non-generic saga surface used by the background workers (watchdog, compensation retry) that
// need to compensate and reason about the distributed-lock budget without knowing the concrete
// saga data type.
public interface ISaga
{
    Task<SagaResult> CompensateAsync(Guid sagaId, CancellationToken cancellationToken);

    // The total time a distributed lock must cover for this saga type:
    // SagaTimeout (forward execution) + CompensationTimeout (worst-case compensation).
    // SagaContinuationEventHandler adds a safety margin on top when computing the lock TTL.
    TimeSpan LockBudget { get; }
}

public interface ISagaBase<TData> : ISaga
    where TData : SagaData

{
    Task<SagaResult> ExecuteAsync(TData data, CancellationToken cancellationToken);

    Task<SagaResult> ResumeFromStepAsync(TData saga, SagaContext context, string fromStepName,
        CancellationToken cancellationToken);
}