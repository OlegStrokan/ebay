using Application;
using Infrastructure;
using Infrastructure.Persistence.DbContext;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace Payment.IntegrationTests.Infrastructure;

public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithDatabase("paymentdb_integration")
        .WithUsername("test")
        .WithPassword("test")
        .WithImage("postgres:16-alpine")
        .Build();

    public IServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));

        services.AddDbContext<PaymentDbContext>(opt =>
            opt.UseNpgsql(_postgres.GetConnectionString()));

        services.AddApplicationServices();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["Stripe:ProviderType"] = "Fake",
                ["Stripe:DefaultCurrency"] = "USD",
                ["Stripe:SecretKey"] = "",
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:SagaTopic"] = "order.events",
                ["Kafka:ProducerClientId"] = "payment-integration-tests",
                ["OrderCallback:PollIntervalSeconds"] = "5",
                ["OrderCallback:BatchSize"] = "100",
                ["ReconciliationWorker:Enabled"] = "true",
            })
            .Build();

        services.AddInfrastructureServices(config);

        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (Services is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        await _postgres.DisposeAsync().AsTask();
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();
}
