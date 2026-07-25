using Application.Interfaces;
using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Application.Sagas.ReturnSaga;
using Grpc.Core;
using Protos.AdminOps;

namespace Api.GrpcServices;

// Internal-only admin surface consumed by the Ops Console service.
// Never routed through the Gateway. Every rpc requires the shared internal
// API key header (x-internal-api-key) to match InternalServices:OpsConsoleApiKey.
//
// Unlike some other internal endpoints in this codebase, this check is
// fail-closed: if the key isn't configured, every call is rejected instead
// of silently allowing unauthenticated access. This service exposes saga
// and dead-letter data (and, in later phases, mutating actions), so an
// unconfigured secret must not fall back to "allow".
public class AdminOpsGrpcService(
    ISagaRepository sagaRepository,
    IDeadLetterRepository deadLetterRepository,
    IFailedCompensationRetryRepository failedCompensationRetryRepository,
    ISagaDistributedLock distributedLock,
    IOrderSaga orderSaga,
    IReturnSaga returnSaga,
    IOrderPersistenceService orderPersistenceService,
    IConfiguration configuration,
    ILogger<AdminOpsGrpcService> logger)
    : AdminOpsService.AdminOpsServiceBase
{
    private const string ApiKeyHeader = "x-internal-api-key";
    private const int MaxTake = 200;

    // Statuses a stuck saga can safely be force-compensated from. Terminal statuses
    // (Completed/Failed/Compensating/Compensated/FailedToCompensate) are excluded —
    // compensating an already-terminal saga risks a double compensation / double refund.
    private static readonly HashSet<SagaStatus> CompensableStatuses =
    [
        SagaStatus.Running,
        SagaStatus.WaitingForEvent,
        SagaStatus.TimedOut
    ];

    public override async Task<GetSagasResponse> GetSagas(
        GetSagasRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        var take = ClampTake(request.Take);
        var skip = Math.Max(request.Skip, 0);

        var (items, totalCount) = await sagaRepository.GetSagasAsync(
            request.Status,
            request.SagaType,
            request.Search,
            skip,
            take,
            context.CancellationToken);

        var response = new GetSagasResponse { TotalCount = totalCount };
        response.Sagas.AddRange(items.Select(ToSummary));
        return response;
    }

    public override async Task<GetSagaResponse> GetSaga(
        GetSagaRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (!Guid.TryParse(request.SagaId, out var sagaId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "saga_id must be a valid GUID."));
        }

        var saga = await sagaRepository.GetByIdAsync(sagaId, context.CancellationToken);

        if (saga is null)
        {
            return new GetSagaResponse { Found = false };
        }

        var order = await orderPersistenceService.LoadOrderAsync(saga.CorrelationId, context.CancellationToken);

        return new GetSagaResponse
        {
            Found = true,
            Id = saga.Id.ToString(),
            CorrelationId = saga.CorrelationId.ToString(),
            SagaType = saga.SagaType,
            Status = saga.Status.ToString(),
            CurrentStep = saga.CurrentStep,
            CreatedAt = saga.CreatedAt.ToString("O"),
            UpdatedAt = saga.UpdatedAt.ToString("O"),
            OrderTrackingId = order?.TrackingId?.Value ?? string.Empty
        };
    }

    public override async Task<GetSagaEventsResponse> GetSagaEvents(
        GetSagaEventsRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (!Guid.TryParse(request.SagaId, out var sagaId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "saga_id must be a valid GUID."));
        }

        var steps = await sagaRepository.GetStepLogsAsync(sagaId, context.CancellationToken);

        var response = new GetSagaEventsResponse();
        response.Steps.AddRange(steps.Select(s => new SagaStepEvent
        {
            StepName = s.StepName,
            Status = s.Status.ToString(),
            ErrorMessage = s.ErrorMessage ?? string.Empty,
            StartedAt = s.StartedAt.ToString("O"),
            CompletedAt = s.CompletedAt?.ToString("O") ?? string.Empty,
            DurationMs = s.DurationMs ?? 0
        }));
        return response;
    }

    public override async Task<GetDeadLettersResponse> GetDeadLetters(
        GetDeadLettersRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        var take = ClampTake(request.Take);
        var skip = Math.Max(request.Skip, 0);

        var messages = await deadLetterRepository.GetAllAsync(skip, take, context.CancellationToken);

        var response = new GetDeadLettersResponse();
        response.Messages.AddRange(messages.Select(m => new DeadLetterSummary
        {
            Id = m.Id.ToString(),
            Type = m.Type,
            AggregateId = m.AggregateId,
            FailureReason = m.FailureReason,
            RetryCount = m.RetryCount,
            MovedToDeadLetterAt = m.MovedToDeadLetterAt.ToString("O")
        }));
        return response;
    }

    public override async Task<MutationResult> CompensateSaga(
        CompensateSagaRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (!Guid.TryParse(request.SagaId, out var sagaId))
        {
            return new MutationResult { Success = false, Message = "saga_id must be a valid GUID." };
        }

        var saga = await sagaRepository.GetByIdAsync(sagaId, context.CancellationToken);
        if (saga is null)
        {
            return new MutationResult { Success = false, Message = "Saga not found." };
        }

        if (!CompensableStatuses.Contains(saga.Status))
        {
            return new MutationResult
            {
                Success = false,
                Message = $"Saga is in status {saga.Status} and cannot be force-compensated from there."
            };
        }

        var sagaInstance = ResolveSaga(saga.SagaType);
        if (sagaInstance is null)
        {
            return new MutationResult { Success = false, Message = $"No saga handler registered for type {saga.SagaType}." };
        }

        // Same lock key / budget the forward, resume, watchdog and compensation-retry paths use,
        // so this admin action can never run concurrently with any of them (post-mortem action #4).
        var lockKey = $"saga-lock:{saga.SagaType}:{saga.CorrelationId}";
        var lockExpiry = sagaInstance.LockBudget + TimeSpan.FromMinutes(1);

        await using var lockHandle = await distributedLock.TryAcquireAsync(lockKey, lockExpiry, context.CancellationToken);
        if (lockHandle is null)
        {
            return new MutationResult
            {
                Success = false,
                Message = "Saga is currently locked by another process (resume/watchdog/retry). Try again shortly."
            };
        }

        // Re-read under the lock: another holder may have already advanced/completed it.
        var current = await sagaRepository.GetByIdAsync(sagaId, context.CancellationToken);
        if (current is null || !CompensableStatuses.Contains(current.Status))
        {
            return new MutationResult
            {
                Success = false,
                Message = $"Saga is now in status {current?.Status.ToString() ?? "unknown"}; no longer eligible."
            };
        }

        current.Status = SagaStatus.Failed;
        current.UpdatedAt = DateTime.UtcNow;
        await sagaRepository.SaveAsync(current, context.CancellationToken);

        logger.LogWarning(
            "Ops Console triggered force-compensation for saga {SagaId} ({SagaType}).",
            sagaId, saga.SagaType);

        try
        {
            var result = await sagaInstance.CompensateAsync(sagaId, context.CancellationToken);
            return new MutationResult
            {
                Success = result.IsSuccess || result.Status == SagaStatus.Compensated,
                Message = result.IsSuccess
                    ? "Compensation completed."
                    : $"Compensation finished with status {result.Status}: {result.ErrorMessage}"
            };
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "CRITICAL: Ops Console-triggered compensation failed for saga {SagaId}.", sagaId);

            current.Status = SagaStatus.FailedToCompensate;
            await sagaRepository.SaveAsync(current, context.CancellationToken);

            return new MutationResult
            {
                Success = false,
                Message = "Compensation threw an exception; saga marked FailedToCompensate. Check logs."
            };
        }
    }

    public override async Task<MutationResult> RetryCompensation(
        RetryCompensationRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (!Guid.TryParse(request.SagaId, out var sagaId))
        {
            return new MutationResult { Success = false, Message = "saga_id must be a valid GUID." };
        }

        var saga = await sagaRepository.GetByIdAsync(sagaId, context.CancellationToken);
        if (saga is null)
        {
            return new MutationResult { Success = false, Message = "Saga not found." };
        }

        if (saga.Status != SagaStatus.FailedToCompensate)
        {
            return new MutationResult
            {
                Success = false,
                Message = $"Saga is in status {saga.Status}, expected FailedToCompensate."
            };
        }

        var now = DateTime.UtcNow;
        var existing = await failedCompensationRetryRepository.GetBySagaIdAsync(sagaId, context.CancellationToken);

        if (existing is not null)
        {
            existing.Reschedule(now, now);
            await failedCompensationRetryRepository.SaveAsync(existing, context.CancellationToken);
        }
        else
        {
            await failedCompensationRetryRepository.EnqueueIfNotExistsAsync(
                sagaId,
                saga.SagaType,
                saga.CurrentStep,
                "Manually retried via Ops Console (no prior retry record found).",
                context.CancellationToken);
        }

        logger.LogWarning(
            "Ops Console scheduled an immediate compensation retry for saga {SagaId} ({SagaType}).",
            sagaId, saga.SagaType);

        return new MutationResult
        {
            Success = true,
            Message = "Compensation retry scheduled; CompensationRetryWorker will pick it up on its next poll."
        };
    }

    public override async Task<MutationResult> RequeueDeadLetter(
        RequeueDeadLetterRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (!Guid.TryParse(request.MessageId, out var messageId))
        {
            return new MutationResult { Success = false, Message = "message_id must be a valid GUID." };
        }

        // DeadLetterRepository.RetryAsync re-inserts the message into the Outbox (same
        // Type/Content/AggregateId), so the already-running OutboxProcessor republishes it
        // to Kafka on its next poll — no new publish logic invoked inline here.
        try
        {
            await deadLetterRepository.RetryAsync(messageId, context.CancellationToken);

            logger.LogWarning(
                "Ops Console requeued dead-letter message {MessageId}.",
                messageId);

            return new MutationResult
            {
                Success = true,
                Message = "Message moved back to the outbox; OutboxProcessor will republish it on its next poll."
            };
        }
        catch (InvalidOperationException ex)
        {
            return new MutationResult { Success = false, Message = ex.Message };
        }
    }

    private ISaga ResolveSaga(string sagaType) => sagaType switch
    {
        SagaTypes.OrderSaga => orderSaga,
        SagaTypes.ReturnSaga => returnSaga,
        _ => null!
    };

    private void EnsureAuthorized(ServerCallContext context)
    {
        var expectedKey = configuration["InternalServices:OpsConsoleApiKey"];

        if (string.IsNullOrEmpty(expectedKey))
        {
            logger.LogError("InternalServices:OpsConsoleApiKey is not configured; rejecting admin ops call.");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller not authorized."));
        }

        var providedKey = context.RequestHeaders.GetValue(ApiKeyHeader);

        if (providedKey != expectedKey)
        {
            logger.LogWarning("Rejected admin ops call with missing/invalid {Header}.", ApiKeyHeader);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller not authorized."));
        }
    }

    private static int ClampTake(int requestedTake)
    {
        if (requestedTake <= 0) return 50;
        return Math.Min(requestedTake, MaxTake);
    }

    private static SagaSummary ToSummary(SagaState s) => new()
    {
        Id = s.Id.ToString(),
        CorrelationId = s.CorrelationId.ToString(),
        SagaType = s.SagaType,
        Status = s.Status.ToString(),
        CurrentStep = s.CurrentStep,
        CreatedAt = s.CreatedAt.ToString("O"),
        UpdatedAt = s.UpdatedAt.ToString("O")
    };
}
