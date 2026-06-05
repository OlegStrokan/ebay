using Application.Gateways;
using Application.Interfaces;
using Application.Sagas;
using Application.Sagas.Handlers;
using Application.Sagas.Persistence;
using Confluent.Kafka;
using Domain.Interfaces;
using Infrastructure.BackgroundServices;
using Infrastructure.Gateways;
using Infrastructure.Gateways.Carrier;
using Infrastructure.Messaging;
using Infrastructure.Persistence;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services;
using Infrastructure.Services.EventIdempotencyChecker;
using Microsoft.EntityFrameworkCore;
using Protos.Accounting;
using Protos.Inventory;
using Protos.Payment;
using Protos.Product;
using Protos.User;
using StackExchange.Redis;

namespace Infrastructure;

public static class InfrastructureModule
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.Configure<ShippingApiOptions>(configuration.GetSection("Shipping"));
        services.Configure<DpdApiOptions>(configuration.GetSection("Shipping:Dpd"));
        services.Configure<PplApiOptions>(configuration.GetSection("Shipping:Ppl"));
        services.Configure<TelegramIncidentReporterOptions>(
            configuration.GetSection(TelegramIncidentReporterOptions.SectionName));
        services.Configure<WriteRoutingOptions>(configuration.GetSection("WriteRouting"));

        services.AddGrpcGatewayClients(configuration);

        services.AddSingleton<IConsumer<string, string>>(sp =>
        {
            var kafkaConfig = new ConsumerConfig
            {
                BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
                GroupId           = configuration["Kafka:ConsumerGroupId"] ?? "order-service",
                AutoOffsetReset   = AutoOffsetReset.Earliest,
                EnableAutoCommit  = false,
                EnableAutoOffsetStore = false,
                IsolationLevel    = IsolationLevel.ReadCommitted
            };
            return new ConsumerBuilder<string, string>(kafkaConfig)
                .SetErrorHandler((_, error) =>
                {
                    var logger = sp.GetRequiredService<ILogger<SagaOrchestrationService>>();
                    logger.LogError("Kafka saga consumer error: {Reason}", error.Reason);
                })
                .Build();
        });

        var redisConnection = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConnection));
        services.AddSingleton<ISagaDistributedLock, RedisSagaDistributedLock>();
        services.AddSingleton<IDomainEventTypeRegistry, DomainEventTypeRegistry>();

        // db context
        services.AddDbContext<AppDbContext>(opt => 
            opt.UseNpgsql(configuration.GetConnectionString("Postgres")));
        
        services.AddDbContext<ReadDbContext>(opt =>
            opt.UseNpgsql(
                configuration.GetConnectionString("PostgresReadModel")
                ?? configuration.GetConnectionString("Postgres")));
        
        // Repositories
        services.AddScoped<IEventStoreRepository, EventStoreRepository>();
        services.AddScoped<ISnapshotRepository, SnapshotRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<ICompensationRefundRetryRepository, CompensationRefundRetryRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<ISagaRepository, SagaRepository>();
        services.AddScoped<IOrderReadRepository, OrderReadRepository>();
        services.AddScoped<IReturnRequestLookupRepository, ReturnRequestLookupRepository>();
        services.AddScoped<IB2BOrderReadRepository, B2BOrderReadRepository>();
        services.AddScoped<IRecurringOrderReadRepository, RecurringOrderReadRepository>();
        services.AddScoped<IKafkaRetryRepository, KafkaRetryRepository>();

        // Services
        services.AddScoped<IOrderPersistenceService, OrderPersistenceService>();
        services.AddScoped<IReturnRequestPersistenceService, ReturnRequestPersistenceService>();
        services.AddScoped<IB2BOrderPersistenceService, B2BOrderPersistenceService>();
        services.AddScoped<IRecurringOrderPersistenceService, RecurringOrderPersistenceService>();
        services.AddScoped<ISagaErrorClassifier, PostgresSagaErrorClassifier>();
        // Singleton: the event-type → handler-type mapping is static; no saga chains are
        // instantiated here, so this is safe to hold for the lifetime of the application.
        services.AddSingleton<ISagaHandlerFactory>(sp =>
            new SagaHandlerFactory(sp.GetServices<SagaHandlerDescriptor>()));
        services.AddScoped<IEventPublisher, KafkaEventPublisher>();
        services.AddScoped<IEventIdempotencyChecker, EventIdempotencyChecker>();
        services.AddSingleton<IReadModelHandlerRegistry, ReadModelHandlerRegistry>();
        services.AddScoped<IReadModelUpdater, OrderReadModelUpdater>();
        services.AddScoped<IReadModelUpdater, ReturnRequestReadModelUpdater>();
        // register as concrete (for direct test resolution) AND as IReadModelUpdater (for routing)
        services.AddScoped<B2BOrderReadModelUpdater>();
        services.AddScoped<IReadModelUpdater>(sp => sp.GetRequiredService<B2BOrderReadModelUpdater>());
        services.AddScoped<RecurringOrderReadModelUpdater>();
        services.AddScoped<IReadModelUpdater>(sp => sp.GetRequiredService<RecurringOrderReadModelUpdater>());
        services.AddScoped<IWriteRegionOwnershipResolver, DeterministicWriteRegionOwnershipResolver>();
        services.AddScoped<IReadModelEventDispatcher, ReadModelEventDispatcher>();

        // Gateways
        services.AddScoped<IProductGateway, ProductGateway>();
        services.AddScoped<IUserGateway, UserGateway>();
        services.AddScoped<IInventoryGateway, InventoryGateway>();
        services.AddScoped<IPaymentGateway, PaymentGateway>();
        services.AddScoped<IShippingWebhookUrlProvider, ShippingWebhookUrlProvider>();
        services.AddHttpClient<DpdShippingAdapter>();
        services.AddHttpClient<PplShippingAdapter>();
        services.AddScoped<IShippingGateway, ShippingGatewayRouter>();
        services.AddScoped<IAccountingGateway, AccountingGateway>();
        services.AddScoped<IEmailGateway, EmailGateway>();
        services.AddHttpClient<TelegramIncidentReporter>();
        services.AddScoped<IIncidentReporter, TelegramIncidentReporter>();

        // Background services
        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<RecurringOrderSchedulerService>();
        services.AddHostedService<SagaOrchestrationService>();
        services.AddHostedService<KafkaReadModelSynchronizer>();
        services.AddHostedService<KafkaReadModelRetryWorker>();
        services.AddHostedService<SagaWatchdogService>();
        services.AddHostedService<ProcessedEventsCleanupService>();
        services.AddHostedService<CompensationRefundRetryWorker>();

        return services;
        
    }

    private static IServiceCollection AddGrpcGatewayClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        RegisterGrpcClient<ProductService.ProductServiceClient>(
            services,
            configuration["GrpcServices:ProductUrl"] ?? "http://product-service:8080");

        RegisterGrpcClient<InventoryService.InventoryServiceClient>(
            services,
            configuration["GrpcServices:InventoryUrl"] ?? "http://inventory-service:8080");

        RegisterGrpcClient<PaymentService.PaymentServiceClient>(
            services,
            configuration["GrpcServices:PaymentUrl"] ?? "http://payment-service:8080");

        RegisterGrpcClient<AccountingService.AccountingServiceClient>(
            services,
            configuration["GrpcServices:AccountingUrl"] ?? "http://accounting-service:8080");

        RegisterGrpcClient<UserServiceProto.UserServiceProtoClient>(
            services,
            configuration["GrpcServices:UserUrl"] ?? "http://user-service:8080");

        return services;
    }

    private static void RegisterGrpcClient<TClient>(
        IServiceCollection services,
        string address)
        where TClient : class
    {
        services.AddGrpcClient<TClient>(options =>
        {
            options.Address = new Uri(address);
        })
        .ConfigureChannel(options =>
        {
            options.HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                EnableMultipleHttp2Connections = true
            };
        });
    }
}