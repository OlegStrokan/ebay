using StackExchange.Redis;

namespace Gateway.Api.Extensions;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }))
            .WithTags("Health")
            .ExcludeFromDescription();

        // Readiness pings Redis (the webhook-dedup store) so a pod that lost it leaves the LB.
        routes.MapGet("/health/ready", async (IConnectionMultiplexer redis) =>
            {
                try
                {
                    await redis.GetDatabase().PingAsync();
                    return Results.Ok(new { status = "Ready" });
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new { status = "Unhealthy", error = ex.Message },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .WithTags("Health")
            .ExcludeFromDescription();

        return routes;
    }
}
