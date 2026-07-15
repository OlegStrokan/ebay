using Application.Interfaces;
using Application.Sagas.Persistence;
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
    IConfiguration configuration,
    ILogger<AdminOpsGrpcService> logger)
    : AdminOpsService.AdminOpsServiceBase
{
    private const string ApiKeyHeader = "x-internal-api-key";
    private const int MaxTake = 200;

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

        return new GetSagaResponse
        {
            Found = true,
            Id = saga.Id.ToString(),
            CorrelationId = saga.CorrelationId.ToString(),
            SagaType = saga.SagaType,
            Status = saga.Status.ToString(),
            CurrentStep = saga.CurrentStep,
            CreatedAt = saga.CreatedAt.ToString("O"),
            UpdatedAt = saga.UpdatedAt.ToString("O")
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
