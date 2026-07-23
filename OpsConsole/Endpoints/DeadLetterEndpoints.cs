using Protos.AdminOps;

namespace OpsConsole.Endpoints;

public static class DeadLetterEndpoints
{
    public static void MapDeadLetterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/deadletters").WithTags("DeadLetters");

        group.MapGet("", async (
            int? skip,
            int? take,
            AdminOpsService.AdminOpsServiceClient client) =>
        {
            var response = await client.GetDeadLettersAsync(new GetDeadLettersRequest
            {
                Skip = skip ?? 0,
                Take = take ?? 50
            });
            return Results.Ok(response);
        });
    }
}
