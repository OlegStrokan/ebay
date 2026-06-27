namespace Application.Sagas;

public interface ISagaBase<TData> 
    where TData : SagaData

{
    Task<SagaResult> ExecuteAsync(TData data, CancellationToken cancellationToken);

    Task<SagaResult> ResumeFromStepAsync(TData saga, SagaContext context, string fromStepName,
        CancellationToken cancellationToken);
    Task<SagaResult> CompensateAsync(Guid sagaId, CancellationToken cancellationToken);

    // The total time a distributed lock must cover for this saga type:
    // SagaTimeout (forward execution) + CompensationTimeout (worst-case compensation).
    // SagaContinuationEventHandler adds a safety margin on top when computing the lock TTL.
    TimeSpan LockBudget { get; }
}