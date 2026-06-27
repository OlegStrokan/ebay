using System.Text.Json;
using Application.Sagas.Handlers.SagaCreation;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.Handlers.SagaContinuation;

public abstract class SagaContinuationEventHandler<TEvent, TData, TContext> 
    : ISagaEventHandler
    where TData : SagaData
    where TContext : SagaContext, new()
{
    private readonly ISagaBase<TData> _saga;
    private readonly ISagaRepository _sagaRepository;
    private readonly ISagaDistributedLock _distributedLock;
    private readonly ILogger _logger;

    // Derived from the saga's own LockBudget (SagaTimeout + CompensationTimeout) so the lock
    // always outlives the full critical section regardless of which saga subclass is used.
    private readonly TimeSpan _lockExpiry;

    // If a concurrent instance holds the lock, retry a few times before giving up.
    // The holder will complete and release quickly in the normal case (duplicate delivery).
    // After retries the saga state check (Completed/Failed) acts as the idempotency guard.
    private const int LockRetryCount = 3;
    private static readonly TimeSpan LockRetryBaseDelay = TimeSpan.FromMilliseconds(200);
    
    public abstract string EventType { get; }
    public abstract string SagaType { get; }
    
    
    protected abstract string ResumeAtStepName { get; }

    protected SagaContinuationEventHandler(
        ISagaBase<TData> saga,
        ISagaRepository sagaRepository,
        ISagaDistributedLock distributedLock,
        ILogger logger)
    {
        _saga = saga;
        _sagaRepository = sagaRepository;
        _distributedLock = distributedLock;
        _logger = logger;
        // Lock must outlive forward execution (SagaTimeout) + worst-case compensation
        // (CompensationTimeout) + a 1-minute safety margin. This is the invariant the
        // review calls "lock TTL > saga timeout + compensation budget".
        _lockExpiry = saga.LockBudget + TimeSpan.FromMinutes(1);
    }
    
    public async Task HandleAsync(string eventPayload, CancellationToken cancellationToken)
    {
        TEvent? eventDto;

        try
        {
            eventDto = JsonSerializer.Deserialize<TEvent>(eventPayload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialize {EventType}. Invalid JSON format.",
                EventType);
            return;
        }

        if (eventDto == null)
        {
            _logger.LogWarning(
                "Failed to deserialize {EventType} - result was null",
                EventType);

            return;
        }

        var correlationId = ExtractCorrelationId(eventDto);

        // Prevents two concurrent events from resuming the same saga at the same time (TOCTOU race).
        // Root causes: duplicate Kafka delivery, webhook retry, multiple app instances.
        // Retry: the holder finishes quickly, so a brief backoff lets us acquire after it releases.
        // If after all retries we still can't acquire, the idempotency status-check below is the
        // secondary guard (saga will be Completed or Failed, so no harm).
        var lockKey = $"saga-lock:{SagaType}:{correlationId}";
        ISagaLockHandle? sagaLock = null;

        for (var attempt = 1; attempt <= LockRetryCount; attempt++)
        {
            sagaLock = await _distributedLock.TryAcquireAsync(lockKey, _lockExpiry, cancellationToken);
            if (sagaLock != null) break;

            _logger.LogWarning(
                "Could not acquire lock for {SagaType} {CorrelationId} on attempt {Attempt}/{Max}. Retrying...",
                SagaType, correlationId, attempt, LockRetryCount);

            if (attempt < LockRetryCount)
                await Task.Delay(LockRetryBaseDelay * attempt, cancellationToken);
        }

        if (sagaLock == null)
        {
            _logger.LogWarning(
                "Could not acquire lock for {SagaType} {CorrelationId} after {Max} attempts. " +
                "Concurrent or duplicate {EventType} event discarded.",
                SagaType, correlationId, LockRetryCount, EventType);
            return;
        }

        await using var _ = sagaLock;

        _logger.LogInformation(
            "Received {EventType} for correlation {CorrelationId}. " +
            "Attempting to resume {SagaType} from step {StepName}...",
            EventType, correlationId, SagaType, ResumeAtStepName);
        
        // find existing saga
        var sagaState = await _sagaRepository.GetByCorrelationIdAsync(
            correlationId,
            SagaType,
            cancellationToken);

        if (sagaState == null)
        {
            _logger.LogError(
                "No {SagaType} found for correlation {CorrelationId}. " +
                "Cannot process {EventType}. This is a critical error!",
                SagaType, correlationId, EventType);
            return;
        }

        if (sagaState.Status != SagaStatus.WaitingForEvent)
        {
            _logger.LogWarning(
                "{SagaType} for {CorrelationId} is in status {Status}, " +
                "expected WaitingForEvent. Event: {EventType}",
                SagaType, correlationId, sagaState.Status, EventType);

            if (sagaState.Status == SagaStatus.Completed)
            {
                _logger.LogInformation(
                    "Saga already completed. This is likely a duplicate webhook/event.");
                return;
            }

            if (sagaState.Status == SagaStatus.Failed)
            {
                _logger.LogWarning(
                    "Saga already failed. Cannot resume");
                return;
            }
        }

        try
        {
            var sagaData = JsonSerializer.Deserialize<TData>(sagaState.Payload);
            var sagaContext = JsonSerializer.Deserialize<TContext>(sagaState.Context);

            if (sagaData == null || sagaContext == null)
            {
                _logger.LogError(
                    "Failed to deserialize saga data/context for {CorrelationId}", correlationId);
                return;
            }

            UpdateContextFromEvent(eventDto, sagaContext);

            _logger.LogInformation(
                "Resuming {SagaType} for {CorrelationId} from step {StepName}",
                SagaType, correlationId, ResumeAtStepName);

            var result = await _saga.ResumeFromStepAsync(
                sagaData,
                sagaContext,
                ResumeAtStepName,
                cancellationToken);

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "{SagaType} resumed successfully for {CorrelationId}",
                    SagaType,
                    correlationId);
            }
            else
            {
                _logger.LogError(
                    "{SagaType} failed during resume for {CorrelationId}: {Error}",
                    SagaType, correlationId, result.ErrorMessage);
            }
        }

        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception while resuming {SagaType} for {CorrelationId}",
                SagaType, correlationId);
        }
        
    }

    protected abstract Guid ExtractCorrelationId(TEvent eventDto);
    protected abstract void UpdateContextFromEvent(TEvent eventDto, TContext context);
}