using Grpc.Net.Client;
using Infrastructure.Messaging;
using Infrastructure.Persistence;
using Inventory.E2ETests.TestHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Protos.Inventory;
using Testcontainers.PostgreSql;
using Xunit;

namespace Inventory.E2ETests.Infrastructure;

[CollectionDefinition("E2E")]
public sealed class E2ECollection : ICollectionFixture<E2ETestServer>
{
}

public sealed class E2ETestServer : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithDatabase("inventorydb_e2e")
        .WithUsername("test")
        .WithPassword("test")
        .WithImage("postgres:16-alpine")
        .Build();

    private string postgresConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        postgresConnectionString = postgres.GetConnectionString();
        await CreateSchemaAsync();
    }

    private async Task CreateSchemaAsync()
    {
        var dbOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(postgresConnectionString)
            .Options;

        await using var dbContext = new InventoryDbContext(dbOptions);
        await dbContext.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("test");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<InventoryDbContext>>();
            services.AddDbContext<InventoryDbContext>(options =>
                options.UseNpgsql(postgresConnectionString));

            services.RemoveAll<IOutboxPublisher>();
            services.AddSingleton<FakeOutboxPublisher>();
            services.AddSingleton<IOutboxPublisher>(sp => sp.GetRequiredService<FakeOutboxPublisher>());
        });

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgresConnectionString,
                ["Outbox:PollIntervalMs"] = "100",
                ["Outbox:BatchSize"] = "20",
                ["Outbox:MaxRetries"] = "5",
                ["Otel:Endpoint"] = "http://localhost:4317"
            }));
    }

    public InventoryService.InventoryServiceClient CreateInventoryClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });

        return new InventoryService.InventoryServiceClient(channel);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await postgres.DisposeAsync();
    }
}
