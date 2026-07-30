using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpsConsole.Auth;

namespace OpsConsole.UnitTests.Auth;

public class ApiKeyMiddlewareTests
{
    private const string HeaderName = "X-Admin-Api-Key";

    private static ApiKeyMiddleware CreateMiddleware(
        string? adminApiKey,
        out Func<bool> wasNextCalled,
        out DefaultHttpContext context)
    {
        var called = false;
        RequestDelegate next = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };
        wasNextCalled = () => called;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AdminApiKey"] = adminApiKey })
            .Build();

        context = new DefaultHttpContext();
        return new ApiKeyMiddleware(next, config, NullLogger<ApiKeyMiddleware>.Instance);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenHeaderMissing()
    {
        var middleware = CreateMiddleware("secret", out var wasNextCalled, out var context);

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(wasNextCalled());
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenKeyDoesNotMatch()
    {
        var middleware = CreateMiddleware("secret", out var wasNextCalled, out var context);
        context.Request.Headers[HeaderName] = "wrong-key";

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(wasNextCalled());
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenKeyMatches()
    {
        var middleware = CreateMiddleware("secret", out var wasNextCalled, out var context);
        context.Request.Headers[HeaderName] = "secret";

        await middleware.InvokeAsync(context);

        Assert.True(wasNextCalled());
        Assert.Equal(200, context.Response.StatusCode); // untouched default, i.e. not short-circuited
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenAdminApiKeyNotConfigured_EvenIfHeaderSent()
    {
        // Fail-closed: an unconfigured secret must never fall back to "allow everything".
        var middleware = CreateMiddleware(adminApiKey: null, out var wasNextCalled, out var context);
        context.Request.Headers[HeaderName] = "anything";

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(wasNextCalled());
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task InvokeAsync_ShouldBypassCheck_ForHealthPaths(string path)
    {
        // Kubelet probes carry no headers at all and can't be configured with the key.
        var middleware = CreateMiddleware(adminApiKey: null, out var wasNextCalled, out var context);
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.True(wasNextCalled());
    }
}
