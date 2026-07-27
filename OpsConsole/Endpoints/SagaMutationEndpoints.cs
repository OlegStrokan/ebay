using System.Security.Claims;
using Grpc.Core;
using Protos.AdminOps;

namespace OpsConsole.Endpoints;

public static class SagaMutationEndpoints
{
    public static void MapSagaMutationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sagas")
            .WithTags("SagaMutations")
            .RequireAuthorization("OpsAdmin")
            .RequireRateLimiting("ops-mutation");

        group.MapPost("/{id}/compensate", async (
            string id,
            ClaimsPrincipal user,
            AdminOpsService.AdminOpsServiceClient client,
            ILoggerFactory loggerFactory) =>
        {
            var audit = loggerFactory.CreateLogger("OpsConsole.Audit");
            var operatorId = GetOperatorId(user);

            audit.LogWarning(
                "AUDIT action=CompensateSaga sagaId={SagaId} operator={Operator} result=attempting",
                id, operatorId);

            try
            {
                var response = await client.CompensateSagaAsync(new CompensateSagaRequest { SagaId = id });

                audit.LogWarning(
                    "AUDIT action=CompensateSaga sagaId={SagaId} operator={Operator} success={Success} message={Message}",
                    id, operatorId, response.Success, response.Message);

                return response.Success ? Results.Ok(response) : Results.Conflict(response);
            }
            catch (RpcException ex)
            {
                // Log the attempt even when the downstream call itself throws (e.g. Order
                // unreachable) — otherwise the audit log silently omits attempts made
                // during an outage, which is exactly when they matter most.
                audit.LogWarning(
                    "AUDIT action=CompensateSaga sagaId={SagaId} operator={Operator} success=false message={Message}",
                    id, operatorId, ex.Status.Detail);
                throw;
            }
        });

        group.MapPost("/{id}/retry-compensation", async (
            string id,
            ClaimsPrincipal user,
            AdminOpsService.AdminOpsServiceClient client,
            ILoggerFactory loggerFactory) =>
        {
            var audit = loggerFactory.CreateLogger("OpsConsole.Audit");
            var operatorId = GetOperatorId(user);

            audit.LogWarning(
                "AUDIT action=RetryCompensation sagaId={SagaId} operator={Operator} result=attempting",
                id, operatorId);

            try
            {
                var response = await client.RetryCompensationAsync(new RetryCompensationRequest { SagaId = id });

                audit.LogWarning(
                    "AUDIT action=RetryCompensation sagaId={SagaId} operator={Operator} success={Success} message={Message}",
                    id, operatorId, response.Success, response.Message);

                return response.Success ? Results.Ok(response) : Results.Conflict(response);
            }
            catch (RpcException ex)
            {
                audit.LogWarning(
                    "AUDIT action=RetryCompensation sagaId={SagaId} operator={Operator} success=false message={Message}",
                    id, operatorId, ex.Status.Detail);
                throw;
            }
        });
    }

    // Structured logging (OpsConsole.Audit category) is the audit trail for now —
    // ship these logs to whatever log aggregation the deployment uses. A persisted
    // in-app audit store/UI was tried in this phase and rolled back as redundant;
    // revisit only if there's no centralized log aggregation to rely on.
    private static string GetOperatorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
