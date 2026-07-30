using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Protos.AdminOps;

namespace OpsConsole.UnitTests.TestHelpers;

// Spins up the real OpsConsole host (real ApiKeyMiddleware, real JwtBearer + OpsViewer/
// OpsAdmin policies, real endpoint routing) with only the three outbound gRPC clients
// swapped for NSubstitute fakes — everything else in Program.cs runs exactly as it does
// in production, so a test proves the actual auth pipeline, not a stand-in for it.
public sealed class OpsConsoleWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminApiKey = "test-admin-api-key";

    // Program.cs reads Jwt:SecretKey/Jwt:Audience into local variables at the top of the
    // script, before builder.Build() runs — ConfigureAppConfiguration below can't reach
    // those values because WebApplicationFactory only layers its overrides in at Build()
    // time, which is too late. So instead of trying to override the secret, mint tokens
    // against whatever appsettings.Development.json actually configures, same as the real
    // Development host will validate against.
    public static readonly (string SecretKey, string Audience) JwtDevConfig = LoadJwtDevConfig();

    private static (string SecretKey, string Audience) LoadJwtDevConfig([CallerFilePath] string thisFile = "")
    {
        var opsConsoleDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(opsConsoleDir, "appsettings.Development.json"), optional: false)
            .Build();

        return (config["Jwt:SecretKey"]!, config["Jwt:Audience"]!);
    }

    public AdminOpsService.AdminOpsServiceClient OrderClient { get; } =
        Substitute.For<AdminOpsService.AdminOpsServiceClient>();

    public AdminPaymentService.AdminPaymentServiceClient PaymentClient { get; } =
        Substitute.For<AdminPaymentService.AdminPaymentServiceClient>();

    public AdminInventoryService.AdminInventoryServiceClient InventoryClient { get; } =
        Substitute.For<AdminInventoryService.AdminInventoryServiceClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // AdminApiKey and InternalServices:OpsConsoleApiKey are read live from
            // IConfiguration at request time, so overriding them here works fine. Jwt:*
            // is deliberately left untouched — see JwtDevConfig above for why.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminApiKey"] = AdminApiKey,
                ["InternalServices:OpsConsoleApiKey"] = "test-internal-key"
            });
        });

        builder.ConfigureServices(services =>
        {
            ReplaceSingleton(services, OrderClient);
            ReplaceSingleton(services, PaymentClient);
            ReplaceSingleton(services, InventoryClient);
        });
    }

    private static void ReplaceSingleton<T>(IServiceCollection services, T instance) where T : class
    {
        services.RemoveAll(typeof(T));
        services.AddSingleton(instance);
    }

    public HttpClient CreateAuthorizedClient(params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", AdminApiKey);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", JwtTokenFactory.CreateToken(JwtDevConfig.SecretKey, JwtDevConfig.Audience, roles));
        return client;
    }

    public HttpClient CreateClientWithoutApiKey(params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", JwtTokenFactory.CreateToken(JwtDevConfig.SecretKey, JwtDevConfig.Audience, roles));
        return client;
    }

    public HttpClient CreateClientWithoutJwt()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", AdminApiKey);
        return client;
    }
}
