using Grpc.Net.Client;
using Infrastructure.Callbacks;
using Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Payment.E2ETests.Infrastructure.Mocks;
using Protos.Payment;
using Testcontainers.PostgreSql;
using Xunit;

namespace Payment.E2ETests.Infrastructure;

public sealed class E2ETestServer : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;

    public FakeOrderCallbackDispatcher CallbackDispatcher { get; } = new();

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithDatabase("paymentdb_e2e")
            .WithUsername("test")
            .WithPassword("admin")
            .WithImage("postgres:16-alpine")
            .Build();

        await _postgres.StartAsync();

        await CreateSchemaAsync();
    }

    private async Task CreateSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var db = new PaymentDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("test");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["Stripe:ProviderType"] = "Fake",
                ["Stripe:SecretKey"] = string.Empty,
                ["Stripe:WebhookSecret"] = string.Empty,
                ["Stripe:DefaultCurrency"] = "USD",
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:SagaTopic"] = "order.events",
                ["Kafka:ProducerClientId"] = "payment-e2e",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<PaymentDbContext>>();
            services.AddDbContext<PaymentDbContext>(opt =>
                opt.UseNpgsql(_postgres.GetConnectionString()));

            services.RemoveAll<IOrderCallbackDispatcher>();
            services.AddSingleton<IOrderCallbackDispatcher>(_ => CallbackDispatcher);
        });
    }

    public PaymentService.PaymentServiceClient CreatePaymentClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });

        return new PaymentService.PaymentServiceClient(channel);
    }

    public HttpClient CreateApiClient() => CreateClient();

    public IServiceScope CreateScope() => Services.CreateScope();

    public void ResetAll()
    {
        CallbackDispatcher.Reset();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}
