using Application.Common.Enums;
using Application.Gateways;
using Application.Interfaces;
using Application.Models;
using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Application.Sagas.ReturnSaga;

namespace Infrastructure.BackgroundServices;

public sealed class CompensationRetryWorker(
    IServiceProvider serviceProvider,
    ILogger<CompensationRetryWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _batchSize = configuration.GetValue<int>("CompensationRetry:BatchSize", 10);
    private readonly int _maxRetries = configuration.GetValue<int>("CompensationRetry:MaxRetries", 5);
    private readonly int _pollIntervalSeconds = configuration.GetValue<int>("CompensationRetry:PollIntervalSeconds", 60);
    private readonly int _baseRetryDelaySeconds = configuration.GetValue<int>("CompensationRetry:BaseRetryDelaySeconds", 60);
    private readonly int _maxRetryDelaySeconds = configuration.GetValue<int>("CompensationRetry:MaxRetryDelaySeconds", 3600);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        logger.LogInformation(
            "CompensationRetryWorker started. BatchSize={BatchSize}, MaxRetries={MaxRetries}, PollIntervalSeconds={PollIntervalSeconds}",
            _batchSize, _maxRetries, _pollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in CompensationRetryWorker loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("CompensationRetryWorker stopped");
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();

        var retryRepository = scope.ServiceProvider.GetRequiredService<IFailedCompensationRetryRepository>();
        var incidentReporter = scope.ServiceProvider.GetRequiredService<IIncidentReporter>();

        var now = DateTime.UtcNow;
        var dueRetries = await retryRepository.ClaimDuePendingAsync(now, _batchSize, cancellationToken);

        if (dueRetries.Count == 0) return;

        foreach (var retry in dueRetries)
        {
            await ProcessRetryAsync(retry, retryRepository, incidentReporter, scope, cancellationToken);
        }
    }

    private async Task ProcessRetryAsync(
        FailedCompensationRetry retry,
        IFailedCompensationRetryRepository retryRepository,
        IIncidentReporter incidentReporter,
        IServiceScope scope,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Retrying compensation for saga {SagaId} ({SagaType}), step {LastFailedStep}. Attempt {Attempt}",
            retry.SagaId, retry.SagaType, retry.LastFailedStep, retry.RetryCount + 1);

        var now = DateTime.UtcNow;

        var sagaInstance = ResolveSaga(retry.SagaType, scope);
        if (sagaInstance is null)
        {
            // Programming error: no handler registered for the saga type. Exhaust immediately.
            logger.LogError(
                "No saga handler registered for type {SagaType}. Exhausting retry for saga {SagaId}.",
                retry.SagaType, retry.SagaId);

            retry.MarkExhausted($"No saga handler registered for type: {retry.SagaType}", now);
            await retryRepository.SaveAsync(retry, cancellationToken);
            await SendExhaustionAlertAsync(retry, $"No saga handler for type {retry.SagaType}", incidentReporter, cancellationToken);
            return;
        }

        // The compensation lock is keyed by correlationId (the order id), but the retry row only
        // carries the saga id — load the saga state to resolve the correlation id.
        var sagaRepository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();
        var sagaState = await sagaRepository.GetByIdAsync(retry.SagaId, cancellationToken);
        if (sagaState is null)
        {
            logger.LogError(
                "Saga state {SagaId} not found for compensation retry. Exhausting.",
                retry.SagaId);

            retry.MarkExhausted("Saga state not found for compensation retry", now);
            await retryRepository.SaveAsync(retry, cancellationToken);
            await SendExhaustionAlertAsync(retry, "Saga state not found", incidentReporter, cancellationToken);
            return;
        }

        // Hold the same distributed lock the forward / resume paths use, so this retry can never
        // run concurrently with a resume, the watchdog, or another retry-worker replica
        // (post-mortem action #4: double compensation -> double refund).
        var distributedLock = scope.ServiceProvider.GetRequiredService<ISagaDistributedLock>();
        var lockKey = $"saga-lock:{retry.SagaType}:{sagaState.CorrelationId}";
        var lockExpiry = sagaInstance.LockBudget + TimeSpan.FromMinutes(1);

        await using var lockHandle = await distributedLock.TryAcquireAsync(lockKey, lockExpiry, cancellationToken);
        if (lockHandle is null)
        {
            // Another worker holds the saga lock. Return the row to Pending for a later attempt
            // without consuming a retry attempt.
            var rescheduleAt = now.AddSeconds(_baseRetryDelaySeconds);
            retry.Reschedule(rescheduleAt, now);
            await retryRepository.SaveAsync(retry, cancellationToken);

            logger.LogInformation(
                "Saga {SagaId} ({SagaType}) is locked by another worker; rescheduled compensation retry for {NextAttempt} (no attempt consumed).",
                retry.SagaId, retry.SagaType, rescheduleAt);
            return;
        }

        var result = await sagaInstance.CompensateAsync(retry.SagaId, cancellationToken);

        if (result.Status == SagaStatus.Compensated)
        {
            retry.MarkCompleted(now);
            await retryRepository.SaveAsync(retry, cancellationToken);

            logger.LogInformation(
                "Compensation retry succeeded for saga {SagaId} ({SagaType}).",
                retry.SagaId, retry.SagaType);
            return;
        }

        // Compensation attempt returned FailedToCompensate again — schedule next retry or exhaust.
        var nextAttemptNumber = retry.RetryCount + 1;
        var errorMessage = result.ErrorMessage ?? "Compensation attempt failed";

        if (nextAttemptNumber < _maxRetries)
        {
            var delay = CalculateRetryDelay(nextAttemptNumber);
            retry.MarkAttemptFailed(errorMessage, now.Add(delay), now);
            await retryRepository.SaveAsync(retry, cancellationToken);

            logger.LogWarning(
                "Compensation retry still failing. SagaId={SagaId}, SagaType={SagaType}, Attempt={Attempt}/{MaxRetries}, NextAttemptAt={NextAttemptAt}",
                retry.SagaId, retry.SagaType, nextAttemptNumber, _maxRetries, retry.NextAttemptAtUtc);
        }
        else
        {
            retry.MarkExhausted(errorMessage, now);
            await retryRepository.SaveAsync(retry, cancellationToken);

            logger.LogCritical(
                "Compensation retry exhausted. SagaId={SagaId}, SagaType={SagaType}, Attempts={Attempts}. Manual intervention required.",
                retry.SagaId, retry.SagaType, retry.RetryCount);

            await SendExhaustionAlertAsync(retry, errorMessage, incidentReporter, cancellationToken);
        }
    }

   private static ISaga? ResolveSaga(string sagaType, IServiceScope scope) => sagaType switch
    {
        SagaTypes.OrderSaga => scope.ServiceProvider.GetService<IOrderSaga>(),
        SagaTypes.ReturnSaga => scope.ServiceProvider.GetService<IReturnSaga>(),
        _ => null,
    };

    private async Task SendExhaustionAlertAsync(
        FailedCompensationRetry retry,
        string lastError,
        IIncidentReporter incidentReporter,
        CancellationToken cancellationToken)
    {
        try
        {
            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "CompensationRetryExhausted",
                    OrderId: retry.SagaId,
                    RefundId: null,
                    Message: $"Saga {retry.SagaType} ({retry.SagaId}) compensation retry exhausted after {retry.RetryCount} attempts. LastFailedStep={retry.LastFailedStep}. LastError={lastError}",
                    Severity: AlertSeverity.Critical),
                cancellationToken);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: retry.SagaId,
                    RefundId: null,
                    Issue: $"Saga compensation permanently stuck: {retry.SagaType} ({retry.SagaId}), step {retry.LastFailedStep}",
                    SuggestedAction: "Manually verify and apply compensation for the failed step, then set saga Status=Compensated in the database."),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to send exhaustion incident alert for saga {SagaId}",
                retry.SagaId);
        }
    }

    private TimeSpan CalculateRetryDelay(int attemptNumber)
    {
        var safeAttempt = Math.Max(1, attemptNumber);
        var growth = Math.Pow(2, safeAttempt - 1);
        var seconds = Math.Min(_maxRetryDelaySeconds, _baseRetryDelaySeconds * growth);
        return TimeSpan.FromSeconds(seconds);
    }
}
