namespace ProductAdmin.Auth;

public class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    private const string Header = "X-Admin-Api-Key";

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue(Header, out var provided) ||
            !string.Equals(provided, config["AdminApiKey"], StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        await next(ctx);
    }
}
