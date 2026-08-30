using Application.Interfaces;
using Domain.Interfaces;
using Infrastructure.BackgroundServices;
using Infrastructure.Options;
using Infrastructure.Persistence.DbContext;
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

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        // read commited low is fine: the unique constraint on TransactionRef,
        // not isolation level, closes the check-then-insert race - a collision becomes
        // DuplicateLedgerTransactionException, which every handler turns into an idempotent success.
        services.AddDbContext<AccountingDbContext>(opt =>
            opt.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

        services.AddScoped<ILedgerTransactionRepository, LedgerTransactionRepository>();
        services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        services.AddHostedService<MoneyEventsConsumerService>();

        return services;
    }
}
