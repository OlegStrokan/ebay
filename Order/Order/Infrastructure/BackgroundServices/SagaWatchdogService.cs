using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Application.Sagas.ReturnSaga;

namespace Infrastructure.BackgroundServices;

public class SagaWatchdogService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SagaWatchdogService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _stuckThreshold = TimeSpan.FromMinutes(5); // "stuck" if no update for 5 mins

    public SagaWatchdogService(
        IServiceProvider serviceProvider,
        ILogger<SagaWatchdogService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        
        _logger.LogInformation(
            "Saga Watchdog started. Poll interval: {PollInterval}, Stuck threshold: {StuckThreshold}",
            _checkInterval, _stuckThreshold);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndRecoverStuckSagaAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Saga Watchdog execution");
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
        } 
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Saga Watchdog cancellation requested.");
        }

        _logger.LogInformation("Saga Watchdog stopped");
    }

    private async Task CheckAndRecoverStuckSagaAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var sagaRepository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();

        _logger.LogDebug("Checking for stuck sagas...");

        var cutoffTime = DateTime.UtcNow - _stuckThreshold;
        var stuckSagas = await sagaRepository.GetStuckSagasAsync(cutoffTime, cancellationToken);

        if (stuckSagas.Count == 0)
        {
            _logger.LogDebug("No stuck sagas found");
            return;
        }

        _logger.LogWarning(
            "Found {Count} stuck sagas haven't updated since {Cutoff}", stuckSagas.Count, cutoffTime);

        foreach (var saga in stuckSagas)
        {
            await HandleStuckSagaAsync(saga, sagaRepository, scope, cancellationToken);
        }
    }


    private async Task HandleStuckSagaAsync(SagaState saga, ISagaRepository sagaRepository, IServiceScope scope, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Processing stuck saga {SagaId} ({SagaType}). " +
            "Correlation: {CorrelationId} Status: {Status}, Current Step: {CurrentStep}, " +
            "Last Updated: {UpdatedAt}",
            saga.Id, saga.SagaType, saga.CorrelationId, saga.Status,
            saga.CurrentStep ?? "None", saga.UpdatedAt);

        try
        {
            // TimedOut sagas skip the tolerance window - SagaBase already set the timeout
            if (saga.Status == SagaStatus.TimedOut)
            {
                _logger.LogWarning(
                    "Saga {SagaId} ({SagaType}) has TimedOut status. Compensating immediately.",
                    saga.Id, saga.SagaType);

                await FailAndCompensateSagaAsync(saga, scope, cancellationToken);
                return;
            }

            if (await IsSagaActuallyCompletedAsync(saga, sagaRepository, cancellationToken))
            {
                _logger.LogInformation(
                    "Saga {SagaId} has all steps completed. Marking as Completed.", saga.Id);

                saga.Status = SagaStatus.Completed;
                saga.UpdatedAt = DateTime.UtcNow;
                await sagaRepository.SaveAsync(saga, cancellationToken);
                return;
            }

            var timeSinceUpdate = DateTime.UtcNow - saga.UpdatedAt;

            if (timeSinceUpdate > _stuckThreshold * 2)
            {
                _logger.LogError(
                    "Saga {SagaId} stuck for {Duration}. Marking as failed and compensating.",
                    saga.Id, timeSinceUpdate);

                await FailAndCompensateSagaAsync(saga, scope, cancellationToken);
                return;
            }

            _logger.LogWarning(
                "Saga {SagaId} stuck {Duration} but within tolerance. " +
                "Will compensate if still stuck on next check.",
                saga.Id, timeSinceUpdate);
        }

        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle stuck saga {SagaId}", saga.Id);
        }
    }

    private async Task<bool> IsSagaActuallyCompletedAsync(
        SagaState saga,
        ISagaRepository sagaRepository,
        CancellationToken cancellationToken)
    {
        if (saga.Status != SagaStatus.Completed) return false;
        
        try
        {
            var steps = await sagaRepository.GetStepLogsAsync(saga.Id, cancellationToken);

            if (!steps.Any())
            {
                _logger.LogDebug("Saga {SagaId} has no steps recorded", saga.Id);
                return false;
            }

            var allCompensated = steps.All(s => s.Status == StepStatus.Completed);

            if (allCompensated)
            {
                _logger.LogInformation(
                    "Saga {SagaId} has all {Count} steps completed", saga.Id, steps.Count);
            }

            return allCompensated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Count not verify completion for saga {SagaId}", saga.Id);
            return false;
        }
    }

    private async Task FailAndCompensateSagaAsync(
        SagaState saga,
        IServiceScope scope,
        CancellationToken cancellationToken)
    {
        var sagaRepository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var sagaInstance = ResolveSaga(saga.SagaType, scope);
        if (sagaInstance is null)
        {
            saga.Status = SagaStatus.Failed;
            saga.UpdatedAt = DateTime.UtcNow;
            await sagaRepository.SaveAsync(saga, cancellationToken);

            _logger.LogError(
                "No saga handler found for {SagaType}. Manual compensation required for saga {SagaId}",
                saga.SagaType, saga.Id);
            return;
        }

        // Compensation must hold the same distributed lock the forward / resume paths use, so the
        // watchdog can never compensate concurrently with an in-flight resume or a second
        // watchdog / retry-worker replica (post-mortem action #4: double compensation -> double refund).
        var distributedLock = scope.ServiceProvider.GetRequiredService<ISagaDistributedLock>();
        var lockKey = $"saga-lock:{saga.SagaType}:{saga.CorrelationId}";
        var lockExpiry = sagaInstance.LockBudget + TimeSpan.FromMinutes(1);

        await using var lockHandle = await distributedLock.TryAcquireAsync(lockKey, lockExpiry, cancellationToken);
        if (lockHandle is null)
        {
            _logger.LogInformation(
                "Saga {SagaId} ({SagaType}) is locked by another worker (resume / watchdog / retry). " +
                "Skipping compensation; it will be re-evaluated on the next watchdog cycle.",
                saga.Id, saga.SagaType);
            return;
        }

        // Re-read under the lock: a concurrent holder may have advanced the saga between the
        // stuck-scan and lock acquisition (e.g. a resume completed it). Only act if it is still
        // in a state the watchdog owns.
        var current = await sagaRepository.GetByIdAsync(saga.Id, cancellationToken);
        if (current is null)
        {
            _logger.LogWarning("Saga {SagaId} no longer exists after acquiring the lock. Skipping.", saga.Id);
            return;
        }

        if (current.Status is not (SagaStatus.Running or SagaStatus.TimedOut))
        {
            _logger.LogInformation(
                "Saga {SagaId} is now {Status} after acquiring the lock. No watchdog action needed.",
                saga.Id, current.Status);
            return;
        }

        current.Status = SagaStatus.Failed;
        current.UpdatedAt = DateTime.UtcNow;
        await sagaRepository.SaveAsync(current, cancellationToken);

        _logger.LogInformation("Attempting to compensate {SagaType} saga {SagaId}", current.SagaType, current.Id);

        try
        {
            await sagaInstance.CompensateAsync(current.Id, cancellationToken);
            _logger.LogInformation("Successfully compensated saga {SagaId}", current.Id);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "CRITICAL: Failed to compensate saga {SagaId}. Manual intervention required!",
                current.Id);

            current.Status = SagaStatus.FailedToCompensate;
            await sagaRepository.SaveAsync(current, cancellationToken);
        }
    }

    private static ISaga? ResolveSaga(string sagaType, IServiceScope scope) => sagaType switch
    {
        SagaTypes.OrderSaga => scope.ServiceProvider.GetService<IOrderSaga>(),
        SagaTypes.ReturnSaga => scope.ServiceProvider.GetService<IReturnSaga>(),
        _ => null,
    };

}