using Application.Gateways;
using Application.Interfaces;
using Domain.Interfaces;
using Infrastructure.BackgroundServices;
using Infrastructure.Callbacks;
using Infrastructure.Gateways;
using Infrastructure.Options;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Persistence.UnitOfWork;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public static class InfrastructureModule
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.SectionName));
        services.Configure<MockFintechOptions>(configuration.GetSection(MockFintechOptions.SectionName));
        services.Configure<OrderCallbackOptions>(configuration.GetSection(OrderCallbackOptions.SectionName));
        services.Configure<ReconciliationWorkerOptions>(configuration.GetSection(ReconciliationWorkerOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<PaymentDbContext>(opt => opt.UseNpgsql(connectionString));

        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IRefundRepository, RefundRepository>();
        services.AddScoped<IPaymentWebhookEventRepository, PaymentWebhookEventRepository>();
        services.AddScoped<IOutboundOrderCallbackRepository, OutboundOrderCallbackRepository>();

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IOrderCallbackPayloadSerializer, OrderCallbackPayloadSerializer>();

        services.AddScoped<StripePaymentProvider>();
        services.AddScoped<FakePaymentProvider>();
        services.AddScoped<MockFintechPaymentProvider>();

        var providerTypeRaw = configuration.GetSection(StripeOptions.SectionName)
            .GetValue<string>("ProviderType")
            ?? throw new InvalidOperationException(
                "Stripe:ProviderType is required. Valid values: Stripe, MockFintech, Fake.");

        if (!Enum.TryParse<PaymentProviderType>(providerTypeRaw, ignoreCase: true, out var providerType))
        {
            throw new InvalidOperationException(
                $"Stripe:ProviderType '{providerTypeRaw}' is not valid. Valid values: Stripe, MockFintech, Fake.");
        }

        services.AddScoped<IStripePaymentProvider>(sp => providerType switch
        {
            PaymentProviderType.Stripe      => sp.GetRequiredService<StripePaymentProvider>(),
            PaymentProviderType.MockFintech => sp.GetRequiredService<MockFintechPaymentProvider>(),
            PaymentProviderType.Fake        => sp.GetRequiredService<FakePaymentProvider>(),
            _ => throw new InvalidOperationException($"Unhandled PaymentProviderType: {providerType}")
        });

        services.AddSingleton<IOrderCallbackDispatcher, OrderCallbackKafkaDispatcher>();

        services.AddHostedService<OrderCallbackDeliveryWorker>();
        services.AddHostedService<PendingPaymentsReconciliationWorker>();

        return services;
    }
}