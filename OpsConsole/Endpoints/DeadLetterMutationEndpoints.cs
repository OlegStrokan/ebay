using System.Security.Claims;
using Grpc.Core;
using Protos.AdminOps;

namespace OpsConsole.Endpoints;

public static class DeadLetterMutationEndpoints
{
    public static void MapDeadLetterMutationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/deadletters")
            .WithTags("DeadLetterMutations")
            .RequireAuthorization("OpsAdmin")
            .RequireRateLimiting("ops-mutation");

        group.MapPost("/{id}/requeue", async (
            string id,
            ClaimsPrincipal user,
            AdminOpsService.AdminOpsServiceClient client,
            ILoggerFactory loggerFactory) =>
        {
            var audit = loggerFactory.CreateLogger("OpsConsole.Audit");
            var operatorId = GetOperatorId(user);

            audit.LogWarning(
                "AUDIT action=RequeueDeadLetter messageId={MessageId} operator={Operator} result=attempting",
                id, operatorId);

            try
            {
                var response = await client.RequeueDeadLetterAsync(new RequeueDeadLetterRequest { MessageId = id });

                audit.LogWarning(
                    "AUDIT action=RequeueDeadLetter messageId={MessageId} operator={Operator} success={Success} message={Message}",
                    id, operatorId, response.Success, response.Message);

                return response.Success ? Results.Ok(response) : Results.Conflict(response);
            }
            catch (RpcException ex)
            {
                audit.LogWarning(
                    "AUDIT action=RequeueDeadLetter messageId={MessageId} operator={Operator} success=false message={Message}",
                    id, operatorId, ex.Status.Detail);
                throw;
            }
        });
    }

    private static string GetOperatorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
