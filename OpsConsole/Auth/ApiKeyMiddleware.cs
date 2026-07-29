namespace OpsConsole.Auth;

// Gates every request from the console frontend/operators. Distinct from the
// x-internal-api-key used between OpsConsole and Order/Payment/Inventory (see
// OpsConsole.Grpc.InternalApiKeyInterceptor) — this one authenticates the
// human/frontend caller, that one authenticates OpsConsole to those services.
public class ApiKeyMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyMiddleware> logger)
{
    private const string Header = "X-Admin-Api-Key";

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Kubernetes liveness/readiness probes hit these paths with no headers at all —
        // they can't carry the admin key, so they must stay reachable unauthenticated.
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next(ctx);
            return;
        }

        var expectedKey = config["AdminApiKey"];

        if (string.IsNullOrEmpty(expectedKey) ||
            !ctx.Request.Headers.TryGetValue(Header, out var provided) ||
            !string.Equals(provided, expectedKey, StringComparison.Ordinal))
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            logger.LogWarning(
                "Admin API key authentication failed. IP={Ip} Path={Path} HeaderPresent={HeaderPresent}",
                ip, ctx.Request.Path, ctx.Request.Headers.ContainsKey(Header));

            ctx.Response.StatusCode = 401;
            return;
        }

        await next(ctx);
    }
}
