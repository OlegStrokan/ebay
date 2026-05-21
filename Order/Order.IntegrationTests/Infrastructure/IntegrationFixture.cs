using Application.Interfaces;
using Application.Sagas;
using Application.Sagas.Persistence;
using Domain.Interfaces;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services;
using Infrastructure.Services.EventIdempotencyChecker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Order.IntegrationTests.TestHelpers;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Order.IntegrationTests.Infrastructure;

/* Shared fixture that starts one PostgreSQL + one Redis TestContainer per test class.
 * model first, no migration needed.
 * Tests are fully isolated without truncating tables - unique aggregate id per test.
 */
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithDatabase("orderdb_integration")
        .WithUsername("test")
        .WithPassword("test")
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public IServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        var services = new ServiceCollection();

        // disable repository INFO logs in test output. keep WARNING and higher visible.
        services.AddLogging(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));

        services.AddDbContext<AppDbContext>(opt =>
            opt.UseNpgsql(_postgres.GetConnectionString()));

        services.AddDbContext<ReadDbContext>(opt =>
            opt.UseNpgsql(_postgres.GetConnectionString()));

        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(_redis.GetConnectionString()));
        services.AddSingleton<ISagaDistributedLock, RedisSagaDistributedLock>();
        services.Configure<WriteRoutingOptions>(opts =>
        {
            opts.Enabled = true;
            opts.CurrentRegion = "eu-west-1";
            opts.Regions = ["eu-west-1", "us-east-1", "ap-southeast-1"];
        });
        services.AddSingleton<IWriteRegionOwnershipResolver, DeterministicWriteRegionOwnershipResolver>();

        services.AddScoped<IEventStoreRepository, EventStoreRepository>();
        services.AddScoped<ISnapshotRepository, SnapshotRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddSingleton<IEventPublisher, FakeEventPublisher>();
        services.AddScoped<IOrderPersistenceService, OrderPersistenceService>();
        services.AddScoped<IReturnRequestPersistenceService, ReturnRequestPersistenceService>();
        services.AddScoped<IB2BOrderPersistenceService, B2BOrderPersistenceService>();
        services.AddScoped<IRecurringOrderPersistenceService, RecurringOrderPersistenceService>();
        services.AddScoped<IOrderReadRepository, OrderReadRepository>();
        services.AddScoped<IB2BOrderReadRepository, B2BOrderReadRepository>();
        services.AddScoped<IRecurringOrderReadRepository, RecurringOrderReadRepository>();
        services.AddScoped<ISagaRepository, SagaRepository>();
        services.AddSingleton<IDomainEventTypeRegistry, DomainEventTypeRegistry>();
        services.AddScoped<IReturnRequestLookupRepository, ReturnRequestLookupRepository>();
        // register as concrete (for direct test resolution) AND as IReadModelUpdater (for routing)
        services.AddScoped<OrderReadModelUpdater>();
        services.AddScoped<ReturnRequestReadModelUpdater>();
        services.AddScoped<B2BOrderReadModelUpdater>();
        services.AddScoped<RecurringOrderReadModelUpdater>();
        services.AddScoped<IReadModelUpdater>(sp => sp.GetRequiredService<OrderReadModelUpdater>());
        services.AddScoped<IReadModelUpdater>(sp => sp.GetRequiredService<ReturnRequestReadModelUpdater>());
        services.AddScoped<IReadModelUpdater>(sp => sp.GetRequiredService<B2BOrderReadModelUpdater>());
        services.AddScoped<IReadModelUpdater>(sp => sp.GetRequiredService<RecurringOrderReadModelUpdater>());
        services.AddScoped<IEventIdempotencyChecker, EventIdempotencyChecker>();
        services.AddSingleton<IReadModelHandlerRegistry, ReadModelHandlerRegistry>();
        services.AddScoped<IKafkaRetryRepository, KafkaRetryRepository>();
        services.AddScoped<IReadModelEventDispatcher, ReadModelEventDispatcher>();

        Services = services.BuildServiceProvider();
        
        // build a full schema
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var readDb = scope.ServiceProvider.GetRequiredService<ReadDbContext>();
        await Task.WhenAll(
            db.Database.EnsureCreatedAsync(),
            readDb.Database.EnsureCreatedAsync());
    }

    public async Task DisposeAsync()
    {
        if (Services is IAsyncDisposable d)
            await d.DisposeAsync();

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
    
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();
}
