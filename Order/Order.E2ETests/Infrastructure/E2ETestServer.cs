using Application.Gateways;
using Application.Interfaces;
using Confluent.Kafka;
using Grpc.Core;
using Grpc.Net.Client;
using Infrastructure.Messaging;
using Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Order.E2ETests.Infrastructure.Mocks;
using ProtoBuf.Meta;
using Protos.Order;
using StackExchange.Redis;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using WireMock.Server;
using Xunit;

// e2e test server with real infra (postgresql, kafka, wireMock);
// uses testContainer for isolated test environment;

namespace Order.E2ETests.Infrastructure;

public class E2ETestServer : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer _postgreSqlContainer = null!;
    private KafkaContainer _kafkaContainer = null!;
    private RedisContainer _redisContainer = null!;
    private WireMockServer _wireMockServer = null!;

    private FakePaymentGrpcServer _paymentService = null!;
    private FakeInventoryGrpcServer _inventoryService = null!;
    private FakeAccountingGrpcServer _accountingService = null!;
    private FakeProductGrpcServer _productService = null!;

    public FakePaymentGrpcServer PaymentService => _paymentService;
    public FakeInventoryGrpcServer InventoryService => _inventoryService;
    public FakeAccountingGrpcServer AccountingServer => _accountingService;
    public FakeProductGrpcServer ProductService => _productService;
    public WireMockServer ShipmentServer => _wireMockServer;

    public string KafkaBootstrapServers { get; private set; } = string.Empty;
    private Func<Guid, WriteRegionOwnershipDecision>? _writeOwnershipOverride;
    

    public async Task InitializeAsync()
    {
        _postgreSqlContainer = new PostgreSqlBuilder()
            .WithDatabase("orderdb_test")
            .WithUsername("test")
            .WithPassword("admin")
            .WithImage("postgres:16-alpine")
            .Build();
        
        _kafkaContainer = new KafkaBuilder()
            .WithImage("confluentinc/cp-kafka:7.7.7")
            .Build();

        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();

        await Task.WhenAll(
            _postgreSqlContainer.StartAsync(),
            _kafkaContainer.StartAsync(),
            _redisContainer.StartAsync());

        KafkaBootstrapServers = _kafkaContainer.GetBootstrapAddress();

        _wireMockServer = WireMockServer.Start();
        _paymentService = new FakePaymentGrpcServer();
        _inventoryService = new FakeInventoryGrpcServer();
        _accountingService = new FakeAccountingGrpcServer();
        _productService = new FakeProductGrpcServer();

        await Task.WhenAll(
            _paymentService.StartAsync(),
            _inventoryService.StartAsync(),
            _accountingService.StartAsync(),
            _productService.StartAsync());
        
        Console.WriteLine("✅ E2E infra ready");
        Console.WriteLine($"   Postgres   : {_postgreSqlContainer.GetConnectionString()}");
        Console.WriteLine($"   Kafka      : {KafkaBootstrapServers}");
        Console.WriteLine($"   Redis      : {_redisContainer.GetConnectionString()}");
        Console.WriteLine($"   Shipping   : {_wireMockServer.Urls[0]}  ← WireMock REST");
        Console.WriteLine($"   Payment    : {_paymentService.Address}");
        Console.WriteLine($"   Inventory  : {_inventoryService.Address}");
        Console.WriteLine($"   Accounting : {_accountingService.Address}");
        Console.WriteLine($"   Product    : {_productService.Address}");

        // Build the DB schema before the host starts so that background services
        // (OutboxProcessor, RecurringOrderSchedulerService, …) find the tables ready.
        await CreateSchemaAsync();
    }

    private async Task CreateSchemaAsync()
    {
        var connStr = _postgreSqlContainer.GetConnectionString();

        var writeOpts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr)
            .Options;
        var readOpts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseNpgsql(connStr)
            .Options;

        await using var appDb  = new AppDbContext(writeOpts);
        await using var readDb = new ReadDbContext(readOpts);
        await appDb.Database.MigrateAsync();
        await readDb.Database.MigrateAsync();

        Console.WriteLine("✅ Schema ready");

    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {

        builder.UseEnvironment("test");
        
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseNpgsql(_postgreSqlContainer.GetConnectionString());
            });

            services.RemoveAll<DbContextOptions<ReadDbContext>>();
            services.AddDbContext<ReadDbContext>(options =>
            {
                options.UseNpgsql(_postgreSqlContainer.GetConnectionString());
            });

            services.Configure<KafkaOptions>(options =>
            {
                options.BootstrapServers = KafkaBootstrapServers;
            });

            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString()));

            services.RemoveAll<IConsumer<string, string>>();
            services.AddSingleton<IConsumer<string, string>>(_ =>
            {
                var config = new ConsumerConfig
                {
                    BootstrapServers      = KafkaBootstrapServers,
                    GroupId               = "order-service",
                    AutoOffsetReset       = AutoOffsetReset.Earliest,
                    EnableAutoCommit      = false,
                    EnableAutoOffsetStore = false,
                    IsolationLevel        = IsolationLevel.ReadCommitted
                };
                return new ConsumerBuilder<string, string>(config).Build();
            });

            services.RemoveAll<IPaymentGateway>();
            services.AddScoped<IPaymentGateway>(_ =>
                new FakeGrpcPaymentGateway(_paymentService.Address));
            
            services.RemoveAll<IInventoryGateway>();
            services.AddScoped<IInventoryGateway>(_ =>
                new FakeGrpcInventoryGateway(_inventoryService.Address));
            
            services.RemoveAll<IAccountingGateway>();
            services.AddScoped<IAccountingGateway>(_ =>
                new FakeGrpcAccountingGateway(_accountingService.Address));

            services.RemoveAll<IProductGateway>();
            services.AddScoped<IProductGateway>(_ =>
                new FakeGrpcProductGateway(_productService.Address));

            services.RemoveAll<IUserGateway>();
            services.AddScoped<IUserGateway>(_ => new FakeUserGateway());

            services.RemoveAll<IShippingGateway>();
            services.AddScoped<IShippingGateway>(_ =>
                new WireMockShippingGateway(_wireMockServer.Urls[0]));

            services.RemoveAll<IWriteRegionOwnershipResolver>();
            services.AddSingleton<IWriteRegionOwnershipResolver>(
                _ => new TestWriteRegionOwnershipResolver(() => _writeOwnershipOverride));
            
            // email-service: not replaced, IEmailGateway published to kafka, tests verify the kafka message
        });
    }

    //---------------helpers---------------//

    public OrderService.OrderServiceClient CreateOrderClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });
        return new OrderService.OrderServiceClient(channel);
    }

    public B2BOrderService.B2BOrderServiceClient CreateB2BOrderClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });
        return new B2BOrderService.B2BOrderServiceClient(channel);
    }

    public RecurringOrderService.RecurringOrderServiceClient CreateRecurringOrderClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });
        return new RecurringOrderService.RecurringOrderServiceClient(channel);
    }

    public T GetService<T>() where T : notnull
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public async Task MigrateDatabaseAsync()
    {
        // Schema is already created in InitializeAsync before the host started.
        // This method is kept for call-site compatibility.
        await Task.CompletedTask;
    }

    public void ResetAll()
    {
        _paymentService.Reset();
        _accountingService.Reset();
        _inventoryService.Reset();
        _productService.Reset();
        _wireMockServer.Reset();
        _writeOwnershipOverride = null;
    }

    public void SetWriteOwnershipOverride(Func<Guid, WriteRegionOwnershipDecision>? resolver)
    {
        _writeOwnershipOverride = resolver;
    }

    public new async Task DisposeAsync()
    {
        await _postgreSqlContainer.DisposeAsync();
        await _kafkaContainer.DisposeAsync();
        await _redisContainer.DisposeAsync();
        await _paymentService.DisposeAsync();
        await _inventoryService.DisposeAsync();
        await _accountingService.DisposeAsync();
        await _productService.DisposeAsync();
        _wireMockServer.Dispose();
        await base.DisposeAsync();
    }

    private sealed class TestWriteRegionOwnershipResolver(
        Func<Func<Guid, WriteRegionOwnershipDecision>?> getOverride)
        : IWriteRegionOwnershipResolver
    {
        public WriteRegionOwnershipDecision ResolveForCustomer(Guid customerId)
        {
            var resolver = getOverride();
            if (resolver is not null)
            {
                return resolver(customerId);
            }

            return new WriteRegionOwnershipDecision(
                IsEnabled: false,
                IsCurrentRegionOwner: true,
                CurrentRegion: "test",
                OwnerRegion: "test");
        }
    }
}