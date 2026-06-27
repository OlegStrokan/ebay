using System.Text.Json;
using Application.Interfaces;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Application.Sagas;

public abstract class SagaBase<TData, TContext> : ISagaBase<TData>
    where TData : SagaData
    where TContext : SagaContext, new()
{
    private readonly ISagaRepository _sagaRepository;
    private readonly ILogger _logger;
    private readonly ISagaErrorClassifier _errorClassifier;
    protected readonly IEnumerable<ISagaStep<TData, TContext>> Steps;
    protected abstract string SagaType { get; }
    protected virtual TimeSpan SagaTimeout => TimeSpan.FromMinutes(5);
    // Maximum time compensation may run. Kept explicit and bounded so that LockBudget
    // is finite and the distributed lock TTL can guarantee no concurrent writer enters
    // while compensation is still running.
    protected virtual TimeSpan CompensationTimeout => TimeSpan.FromMinutes(3);
    // The total time a distributed lock must cover: forward execution (SagaTimeout) +
    // worst-case compensation (CompensationTimeout). SagaContinuationEventHandler adds
    // a safety margin on top when computing the Redis lock TTL.
    public TimeSpan LockBudget => SagaTimeout + CompensationTimeout;

    protected SagaBase(
        ISagaRepository sagaRepository,
        IEnumerable<ISagaStep<TData, TContext>> steps,
        ISagaErrorClassifier errorClassifier,
        ILogger logger)
    {
        _sagaRepository = sagaRepository;
        _logger = logger;
        _errorClassifier = errorClassifier;
        Steps = steps.OrderBy(s => s.Order).ToList();
    }

    public async Task<SagaResult> ExecuteAsync(TData data, CancellationToken cancellationToken)
    {
        /* @todo: we will add like 400 lines of divined code to create event
           driven stuff type shit yes we have basic logs we have logs,
           but if Berezovsky will lose money because of us?
         
           current implementation: it's too complex so we use SagaWatchdogService for pooling data
           but when berezovsky will come - gotta be fucking prepared
        */

        /* serviceCancellationToken is the host lifetime token - stays live until the process stops
           sagaCancellationToken is linked to both: it fires on timeout OR service shutdown
           
           Cleanup after timeout must use serviceCancellationToken, not sagaCancellationToken,
           because sagaCancellationToken is already canceled when the timeout fires
        */
        
        var serviceCancellationToken = cancellationToken;
        using var timeoutCts = new CancellationTokenSource(SagaTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken, timeoutCts.Token);
        var sagaCancellationToken = linkedCts.Token;

        var sagaId = Guid.NewGuid();
        var context = new TContext(); 

        _logger.LogInformation(
            "Starting {SagaType} for correlation {CorrelationId} with {StepCount} steps",
            SagaType, data.CorrelationId, Steps.Count());

        var sagaState = new SagaState
        {
            Id = sagaId,
            CorrelationId = data.CorrelationId,
            Status = SagaStatus.Running,
            SagaType = SagaType,
            Payload = JsonSerializer.Serialize(data),
            Context = JsonSerializer.Serialize(context), 
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await _sagaRepository.SaveAsync(sagaState, sagaCancellationToken);

        _logger.LogInformation(
            "Started {SagaType} saga {SagaId} for correlation {CorrelationId}",
            SagaType, sagaId, data.CorrelationId);

        try
        {
            foreach (var step in Steps)
            {
                _logger.LogInformation(
                    "Executing step {StepName} (order: {Order}) for {CorrelationId}",
                    step.StepName, step.Order, data.CorrelationId);

                var stepResult = await ExecuteStepAsync(
                    sagaId,
                    step,
                    data,
                    context,
                    sagaCancellationToken);

                sagaState.Context = JsonSerializer.Serialize(context);
                sagaState.CurrentStep = step.StepName;
                sagaState.UpdatedAt = DateTime.UtcNow;

                if (stepResult is WaitForEvent)
                {
                    _logger.LogInformation(
                        "Step {StepName} marked saga as waiting for external event.",
                        step.StepName);

                    sagaState.Status = SagaStatus.WaitingForEvent;
                    await _sagaRepository.SaveAsync(sagaState, sagaCancellationToken);

                    return SagaResult.Success(sagaId);
                }

                if (stepResult is Fail stepFailure)
                {
                    _logger.LogWarning(
                        "Step {StepName} failed in saga {SagaId}. Starting compensation...",
                        step.StepName, sagaId);

                    sagaState.Status = SagaStatus.Failed;
                    await _sagaRepository.SaveAsync(sagaState, sagaCancellationToken);

                    // Use serviceCancellationToken, not sagaCancellationToken: a step that
                    // fails near the SagaTimeout boundary would leave compensation almost no
                    // time. CompensateAsync adds its own CompensationTimeout bound internally.
                    await CompensateAsync(sagaId, serviceCancellationToken);
                    return SagaResult.Failed(sagaId, stepFailure.Reason);
                }

                await _sagaRepository.SaveAsync(sagaState, sagaCancellationToken);
            }

            sagaState.Status = SagaStatus.Completed;
            sagaState.UpdatedAt = DateTime.UtcNow;
            await _sagaRepository.SaveAsync(sagaState, sagaCancellationToken);

            _logger.LogInformation("Saga {SagaId} completed successfully", sagaId);
            return SagaResult.Success(sagaId);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !serviceCancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Saga {SagaId} ({SagaType}) timed out after {Timeout}. Starting compensation.",
                sagaId, SagaType, SagaTimeout);

            sagaState.Status = SagaStatus.TimedOut;
            sagaState.UpdatedAt = DateTime.UtcNow;
            await _sagaRepository.SaveAsync(sagaState, serviceCancellationToken);

            await CompensateAsync(sagaId, serviceCancellationToken);
            return SagaResult.TimedOut(sagaId);
        }
    }

    public async Task<SagaResult> ResumeFromStepAsync(
        TData data,
        SagaContext context,
        string fromStepName,
        CancellationToken cancellationToken)
    {
        var serviceCancellationToken = cancellationToken;
        using var timeoutCts = new CancellationTokenSource(SagaTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken, timeoutCts.Token);
        var resumeCancellationToken = linkedCts.Token;

        _logger.LogInformation(
            "Resuming {SagaType} for correlation {CorrelationId} from step {StepName}",
            SagaType, data.CorrelationId, fromStepName);

        var typedContext = (TContext)context; // Safe because of generic constraints
        
        var sagaState = await _sagaRepository.GetByCorrelationIdAsync(
            data.CorrelationId,
            SagaType,
            resumeCancellationToken);

        if (sagaState == null)
        {
            _logger.LogError(
                "Cannot resume {SagaType} for {CorrelationId} - saga state not found",
                SagaType, data.CorrelationId);
            return SagaResult.Failed(Guid.Empty, "Saga state not found");
        }

        if (sagaState.Status != SagaStatus.WaitingForEvent)
        {
            _logger.LogWarning(
                "Saga {SagaId} is in status {Status}, expected WaitingForEvent.",
                sagaState.Id, sagaState.Status);

            if (sagaState.Status == SagaStatus.Completed)
            {
                return SagaResult.Success(sagaState.Id);
            }

            return SagaResult.Failed(sagaState.Id, $"Saga not in waiting state: {sagaState.Status}");
        }

        sagaState.Status = SagaStatus.Running;
        sagaState.UpdatedAt = DateTime.UtcNow;
        sagaState.Context = JsonSerializer.Serialize(typedContext);   
        
        await _sagaRepository.SaveAsync(sagaState, resumeCancellationToken);

        var resumeStep = Steps.FirstOrDefault(s => s.StepName == fromStepName);
        if (resumeStep == null)
        {
            return SagaResult.Failed(sagaState.Id, $"Step {fromStepName} not found");
        }

        var remainingSteps = Steps
            .Where(s => s.Order >= resumeStep.Order)
            .OrderBy(s => s.Order)
            .ToList();

        // Steps that completed during a previous (interrupted) resume pass can be skipped.
        // The resume step itself (fromStepName) must always run - it previously returned
        // WaitForEvent and its step log is therefore also marked Completed, so we exclude
        // it from the skip-set explicitly
        var alreadyCompleted = sagaState.Steps
            .Where(s => s.Status == StepStatus.Completed && s.StepName != fromStepName)
            .Select(s => s.StepName)
            .ToHashSet();

        try
        {
            foreach (var step in remainingSteps)
            {
                if (alreadyCompleted.Contains(step.StepName))
                {
                    _logger.LogInformation(
                        "Skipping already-completed step {StepName} during resume of saga {SagaId}",
                        step.StepName, sagaState.Id);
                    continue;
                }

                var stepResult = await ExecuteStepAsync(
                    sagaState.Id, step, data, typedContext, resumeCancellationToken);

                sagaState.Context = JsonSerializer.Serialize(typedContext); 
                sagaState.CurrentStep = step.StepName;
                sagaState.UpdatedAt = DateTime.UtcNow;

                if (stepResult is WaitForEvent)
                {
                    sagaState.Status = SagaStatus.WaitingForEvent;
                    await _sagaRepository.SaveAsync(sagaState, resumeCancellationToken);
                    return SagaResult.Success(sagaState.Id);
                }

                if (stepResult is Fail resumeFailure)
                {
                    // Use serviceCancellationToken: resumeCancellationToken may be near timeout
                    await CompensateAsync(sagaState.Id, serviceCancellationToken);
                    return SagaResult.Failed(sagaState.Id, resumeFailure.Reason);
                }

                await _sagaRepository.SaveAsync(sagaState, resumeCancellationToken);
            }

            sagaState.Status = SagaStatus.Completed;
            sagaState.UpdatedAt = DateTime.UtcNow;
            await _sagaRepository.SaveAsync(sagaState, resumeCancellationToken);

            return SagaResult.Success(sagaState.Id);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !serviceCancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Resumed saga {SagaId} ({SagaType}) timed out after {Timeout}. Starting compensation.",
                sagaState.Id, SagaType, SagaTimeout);

            sagaState.Status = SagaStatus.TimedOut;
            sagaState.UpdatedAt = DateTime.UtcNow;
            await _sagaRepository.SaveAsync(sagaState, serviceCancellationToken);

            await CompensateAsync(sagaState.Id, serviceCancellationToken);
            return SagaResult.TimedOut(sagaState.Id);
        }
    }

    private async Task<StepOutcome> ExecuteStepAsync(
        Guid sagaId,
        ISagaStep<TData, TContext> step,
        TData data,
        TContext context,
        CancellationToken cancellationToken)
    {
        //  all retry is very different so copy-paste approach is ok
        
        const int maxRetries = 3;
        int retryCount = 0;

        while (true)
        {

            var stepLog = new SagaStepLog
            {
                Id = Guid.NewGuid(),
                SagaId = sagaId,
                StepName = step.StepName,
                Status = StepStatus.Running,
                StartedAt = DateTime.UtcNow
            };

            try
            {
                var startTime = DateTime.UtcNow;
                var result = await step.ExecuteAsync(data, context, cancellationToken);
                var endTime = DateTime.UtcNow;

                stepLog.CompletedAt = endTime;
                stepLog.DurationMs = (int)(endTime - startTime).TotalMilliseconds;
                stepLog.Status = result is Fail ? StepStatus.Failed : StepStatus.Completed;
                stepLog.ErrorMessage = result is Fail failResult ? failResult.Reason : null;

                await _sagaRepository.SaveStepAsync(stepLog, cancellationToken);

                return result;
            }

            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (_errorClassifier.IsTransient(ex) && retryCount < maxRetries)
            {
                retryCount++;
                _logger.LogWarning(ex, "Transient error in {StepName}. Retry {Count}/{Max}",
                    step.StepName, retryCount, maxRetries);

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in step {StepName} of saga {SagaId}", step.StepName, sagaId);

                stepLog.Status = StepStatus.Failed;
                stepLog.ErrorMessage = ex.Message;
                stepLog.CompletedAt = DateTime.UtcNow;

                await _sagaRepository.SaveStepAsync(stepLog, cancellationToken);

                return new Fail(ex.Message);
            }
        }
    }

    public async Task<SagaResult> CompensateAsync(Guid sagaId, CancellationToken cancellationToken)
    {
        // Compensation must complete within CompensationTimeout. Without this bound an
        // unbounded compensation run can outlast the distributed lock TTL, creating a
        // window where a concurrent resumer acquires the lock mid-compensation and races
        // with it (double-compensation / refund+re-charge interleave).
        using var compensationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        compensationCts.CancelAfter(CompensationTimeout);
        var ct = compensationCts.Token;

        var sagaState = await _sagaRepository.GetByIdAsync(sagaId, ct);
        if (sagaState == null)
            throw new InvalidOperationException($"Saga {sagaId} not found");

        if (sagaState.Status == SagaStatus.Compensated)
        {
            return SagaResult.Compensated(sagaId);
        }

        sagaState.Status = SagaStatus.Compensating;
        await _sagaRepository.SaveAsync(sagaState, ct);

        var data = JsonSerializer.Deserialize<TData>(sagaState.Payload)!;
        var context = JsonSerializer.Deserialize<TContext>(sagaState.Context) ?? new TContext();
        
        var completedSteps = sagaState.Steps
            .Where(s => s.Status == StepStatus.Completed)
            .Select(s => s.StepName)
            .ToHashSet();

        foreach (var step in Steps.Reverse())
        {
            if (!completedSteps.Contains(step.StepName))
                continue;

            int retryCount = 0;
            const int maxRetries = 3;

            while (true)
            {
                try
                {
                    await step.CompensateAsync(data, context, ct);

                    var stepLog = sagaState.Steps.First(s => s.StepName == step.StepName);
                    stepLog.Status = StepStatus.Compensated;
                    await _sagaRepository.SaveCompensationStateAsync(
                        sagaState, stepLog, ct);

                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (_errorClassifier.IsTransient(ex) && retryCount < maxRetries)
                {
                    retryCount++;
                    _logger.LogWarning(ex, "Compensation transient failure for {StepName}. Retry {Count}/{Max}",
                        step.StepName, retryCount, maxRetries);

                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "FATAL: Compensation failed permanently for {StepName} in saga {SagaId}",
                        step.StepName, sagaId);

                    sagaState.Status = SagaStatus.FailedToCompensate;
                    await _sagaRepository.SaveAsync(sagaState, ct);
                    throw;
                }
            }
        }

        sagaState.Status = SagaStatus.Compensated;
        sagaState.UpdatedAt = DateTime.UtcNow;
        await _sagaRepository.SaveAsync(sagaState, ct);

        return SagaResult.Compensated(sagaId);
    }
}