using Application.Gateways;
using Application.Interfaces;
using Domain.Interfaces;
using Infrastructure.BackgroundServices;
using Infrastructure.Gateways;
using Infrastructure.Monitoring;
using Infrastructure.Options;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Queries;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Persistence.UnitOfWork;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public static class InfrastructureModule
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.Configure<MoneyEventConsumerOptions>(
            configuration.GetSection(MoneyEventConsumerOptions.SectionName));
        services.Configure<ReconciliationOptions>(
            configuration.GetSection(ReconciliationOptions.SectionName));
        services.Configure<ReportingOptions>(
            configuration.GetSection(ReportingOptions.SectionName));
        services.Configure<TelegramIncidentReporterOptions>(
            configuration.GetSection(TelegramIncidentReporterOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        // read commited low is fine: the unique constraint on TransactionRef,
        // not isolation level, closes the check-then-insert race - a collision becomes
        // DuplicateLedgerTransactionException, which every handler turns into an idempotent success.
        services.AddDbContext<AccountingDbContext>(opt =>
            opt.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

        services.AddScoped<ILedgerTransactionRepository, LedgerTransactionRepository>();
        services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();
        services.AddScoped<ILedgerReconciliationReader, LedgerReconciliationReader>();
        services.AddScoped<ILedgerReportingReader, LedgerReportingReader>();
        services.AddScoped<IFxRateRepository, FxRateRepository>();
        services.AddScoped<ILedgerReportingEntryRepository, LedgerReportingEntryRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        services.AddSingleton<IMoneyEventConsumerMonitor, MoneyEventConsumerMonitor>();

        services.AddHttpClient<TelegramIncidentReporter>();
        services.AddScoped<IIncidentReporter, TelegramIncidentReporter>();

        services.AddHostedService<MoneyEventsConsumerService>();
        services.AddHostedService<ReconcileLedgerWorker>();
        services.AddHostedService<ReportingProjectionWorker>();

        return services;
    }
}
