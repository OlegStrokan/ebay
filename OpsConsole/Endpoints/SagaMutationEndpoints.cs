using System.Security.Claims;
using Protos.AdminOps;

namespace OpsConsole.Endpoints;

public static class SagaMutationEndpoints
{
    public static void MapSagaMutationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sagas")
            .WithTags("SagaMutations")
            .RequireAuthorization("OpsAdmin");

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

            var response = await client.CompensateSagaAsync(new CompensateSagaRequest { SagaId = id });

            audit.LogWarning(
                "AUDIT action=CompensateSaga sagaId={SagaId} operator={Operator} success={Success} message={Message}",
                id, operatorId, response.Success, response.Message);

            return response.Success ? Results.Ok(response) : Results.Conflict(response);
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

            var response = await client.RetryCompensationAsync(new RetryCompensationRequest { SagaId = id });

            audit.LogWarning(
                "AUDIT action=RetryCompensation sagaId={SagaId} operator={Operator} success={Success} message={Message}",
                id, operatorId, response.Success, response.Message);

            return response.Success ? Results.Ok(response) : Results.Conflict(response);
        });
    }

    // Structured logging is the audit trail for now (no DB in this service by design —
    // see Phase 1 notes). Ship these logs to whatever log aggregation the deployment uses
    // (OpenTelemetry/ELK); formalize into a persisted, queryable audit store in Phase 7
    // hardening if that's not sufficient.
    private static string GetOperatorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
