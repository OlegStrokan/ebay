using Application.Commands.ProjectReportingEntries;
using Application.Interfaces;
using Domain.Entities;
using Domain.Interfaces;
using Infrastructure.Options;
using MediatR;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

internal sealed class ReportingProjectionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReportingOptions> options,
    ILogger<ReportingProjectionWorker> logger) : BackgroundService
{
    private readonly ReportingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning(
                "ReportingProjectionWorker is disabled. No reporting-currency trial balance will be produced.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(NormalizePositive(_options.StartupDelaySeconds, 30)), stoppingToken);

        await SeedRatesAsync(stoppingToken);

        var interval = TimeSpan.FromMinutes(NormalizePositive(_options.IntervalMinutes, 5));

        logger.LogInformation(
            "ReportingProjectionWorker started. ReportingCurrency={ReportingCurrency}, Interval={IntervalMinutes}m",
            _options.ReportingCurrency,
            interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                await mediator.Send(
                    new ProjectReportingEntriesCommand(_options.ReportingCurrency, _options.BatchSize),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reporting projection cycle failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("ReportingProjectionWorker stopped.");
    }

    private async Task SeedRatesAsync(CancellationToken cancellationToken)
    {
        if (_options.SeedRates.Count == 0)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var rates = scope.ServiceProvider.GetRequiredService<IFxRateRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var added = 0;

            foreach (var seed in _options.SeedRates)
            {
                if (await rates.ExistsAsync(seed.BaseCurrency, seed.QuoteCurrency, seed.EffectiveFrom, cancellationToken))
                    continue;

                await rates.AddAsync(
                    FxRate.Create(seed.BaseCurrency, seed.QuoteCurrency, seed.Rate, seed.EffectiveFrom),
                    cancellationToken);
                added++;
            }

            if (added > 0)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Seeded {Count} FX rate(s) from configuration.", added);
            }
        }
        catch (Exception ex)
        {
            // Missing rates only stall the projection; they never corrupt the primitive ledger.
            logger.LogError(ex, "Failed to seed FX rates from configuration.");
        }
    }

    private static int NormalizePositive(int value, int fallback) => value > 0 ? value : fallback;
}
