using Grpc.Net.Client;
using Infrastructure.Messaging;
using Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Protos.Product;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Xunit;

namespace Product.E2ETests.Infrastructure;

[CollectionDefinition("E2E")]
public class E2ECollection : ICollectionFixture<E2ETestServer> { }

public class E2ETestServer : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithDatabase("productdb_e2e")
        .WithUsername("test")
        .WithPassword("test")
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.7.7")
        .Build();

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());

        KafkaBootstrapServers = _kafka.GetBootstrapAddress();

        // Build schema before the factory host boots so EF tables exist when
        // background services (OutboxProcessor) start their first poll.
        await CreateSchemaAsync();

        Console.WriteLine("E2E infra ready");
        Console.WriteLine($"Postgres: {_postgres.GetConnectionString()}");
        Console.WriteLine($"Kafka: {KafkaBootstrapServers}");
    }

    private async Task CreateSchemaAsync()
    {
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var db = new ProductDbContext(opts);
        await db.Database.MigrateAsync();

        Console.WriteLine("Schema ready");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("test");

        builder.ConfigureTestServices(services =>
        {
            // Replace DbContext to point at the test container
            services.RemoveAll<DbContextOptions<ProductDbContext>>();
            services.AddDbContext<ProductDbContext>(opt =>
                opt.UseNpgsql(_postgres.GetConnectionString()));

            // Point the Kafka producer at the test container
            services.Configure<KafkaOptions>(opt =>
                opt.BootstrapServers = KafkaBootstrapServers);
        });

        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:PollIntervalMs"] = "200",
                // Silence the OTLP exporter - it fails silently in test but logs noise
                ["Otel:Endpoint"] = "http://localhost:4317"
            }));
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _kafka.DisposeAsync().AsTask());
    }

    // Creates a gRPC client that talks to the in-process test server over HTTP/2.
    public ProductService.ProductServiceClient CreateProductClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });
        return new ProductService.ProductServiceClient(channel);
    }

    public T GetService<T>() where T : notnull
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }
}
