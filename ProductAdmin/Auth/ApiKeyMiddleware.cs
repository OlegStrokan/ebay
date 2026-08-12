using System.Security.Cryptography;
using System.Text;

namespace ProductAdmin.Auth;

public class ApiKeyMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyMiddleware> logger)
{
    private const string Header = "X-Admin-Api-Key";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var expectedKey = config["AdminApiKey"] ?? string.Empty;

        if (!ctx.Request.Headers.TryGetValue(Header, out var provided) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided.ToString()),
                Encoding.UTF8.GetBytes(expectedKey)))
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
