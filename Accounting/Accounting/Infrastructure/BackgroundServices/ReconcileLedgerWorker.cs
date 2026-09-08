using Application.Commands.ReconcileLedger;
using Infrastructure.Options;
using MediatR;
using Microsoft.Extensions.Options;

namespace Infrastructure.BackgroundServices;

internal sealed class ReconcileLedgerWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconciliationOptions> options,
    ILogger<ReconcileLedgerWorker> logger) : BackgroundService
{
    private readonly ReconciliationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning(
                "ReconcileLedgerWorker is disabled. Ledger drift will go undetected while it stays off.");
            return;
        }

        // Let migrations finish and the consumer drain its backlog before judging the totals.
        var startupDelay = NormalizePositive(_options.StartupDelaySeconds, 60);
        await Task.Delay(TimeSpan.FromSeconds(startupDelay), stoppingToken);

        var interval = TimeSpan.FromMinutes(NormalizePositive(_options.IntervalMinutes, 15));

        logger.LogInformation(
            "ReconcileLedgerWorker started. Interval={IntervalMinutes}m, MaxAcceptableLag={MaxAcceptableLag}",
            interval.TotalMinutes,
            _options.MaxAcceptableLag);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                await mediator.Send(
                    new ReconcileLedgerCommand(_options.MaxFindings, _options.MaxAcceptableLag),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed check is not drift, but it does mean nothing is watching this cycle.
                logger.LogError(ex, "Ledger reconciliation cycle failed.");
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

        logger.LogInformation("ReconcileLedgerWorker stopped.");
    }

    private static int NormalizePositive(int value, int fallback) => value > 0 ? value : fallback;
}
