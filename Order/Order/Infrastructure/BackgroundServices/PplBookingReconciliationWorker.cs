using Application.Gateways;
using Application.Interfaces;
using Application.Models;
using Domain.ValueObjects;

namespace Infrastructure.BackgroundServices;

/// Background worker that retries PPL booking status checks every 30 minutes when the
/// initial two-phase poll window in CreateShipmentStep was exhausted without a
/// settled response. On acceptance it updates the order tracking. On exhaustion it fires
/// a Telegram intervention ticket for manual follow-up.
public sealed class PplBookingReconciliationWorker(
    IServiceProvider serviceProvider,
    ILogger<PplBookingReconciliationWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _batchSize =
        configuration.GetValue<int>("PplBookingReconciliation:BatchSize", 10);
    private readonly int _maxAttempts =
        configuration.GetValue<int>("PplBookingReconciliation:MaxAttempts", 48); // ~24 h at 30 min
    private readonly int _pollIntervalSeconds =
        configuration.GetValue<int>("PplBookingReconciliation:PollIntervalSeconds", 1800);
    private readonly int _retryIntervalSeconds =
        configuration.GetValue<int>("PplBookingReconciliation:RetryIntervalSeconds", 1800);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        logger.LogInformation(
            "PplBookingReconciliationWorker started. BatchSize={BatchSize}, MaxAttempts={MaxAttempts}, PollIntervalSeconds={PollIntervalSeconds}",
            _batchSize, _maxAttempts, _pollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in PplBookingReconciliationWorker loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("PplBookingReconciliationWorker stopped");
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();

        var bookingRepository = scope.ServiceProvider.GetRequiredService<IPplPendingBookingRepository>();
        var bookingPoller = scope.ServiceProvider.GetRequiredService<IPplBookingPoller>();
        var orderPersistenceService = scope.ServiceProvider.GetRequiredService<IOrderPersistenceService>();
        var incidentReporter = scope.ServiceProvider.GetRequiredService<IIncidentReporter>();

        var now = DateTime.UtcNow;
        var due = await bookingRepository.ClaimDuePendingAsync(now, _batchSize, cancellationToken);

        if (due.Count == 0) return;

        foreach (var booking in due)
        {
            await ProcessBookingAsync(
                booking, bookingRepository, bookingPoller,
                orderPersistenceService, incidentReporter, cancellationToken);
        }
    }

    private async Task ProcessBookingAsync(
        PplPendingBooking booking,
        IPplPendingBookingRepository bookingRepository,
        IPplBookingPoller bookingPoller,
        IOrderPersistenceService orderPersistenceService,
        IIncidentReporter incidentReporter,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingPoller.PollAsync(booking.ReferenceId, cancellationToken);

            switch (result.Status)
            {
                case PplBookingPollStatus.Accepted:
                    await orderPersistenceService.UpdateOrderAsync(
                        booking.OrderId,
                        order =>
                        {
                            order.AssignTracking(TrackingId.From(result.TrackingNumber!));
                            return Task.CompletedTask;
                        },
                        cancellationToken);

                    booking.MarkAccepted(DateTime.UtcNow);
                    await bookingRepository.SaveAsync(booking, cancellationToken);

                    logger.LogInformation(
                        "PplBookingReconciliation: booking accepted. OrderId={OrderId}, ReferenceId={ReferenceId}, ParcelId={ParcelId}, TrackingNumber={TrackingNumber}",
                        booking.OrderId, booking.ReferenceId, result.ParcelId, result.TrackingNumber);
                    break;

                case PplBookingPollStatus.Rejected:
                    await EscalateAsync(
                        booking, bookingRepository, incidentReporter,
                        $"PPL rejected booking {booking.ReferenceId}. Reason: {result.Reason ?? "unspecified"}",
                        cancellationToken);
                    break;

                case PplBookingPollStatus.Pending:
                    var nextAttempt = booking.AttemptCount + 1;

                    if (nextAttempt >= _maxAttempts)
                    {
                        await EscalateAsync(
                            booking, bookingRepository, incidentReporter,
                            $"PPL booking {booking.ReferenceId} remained pending for {nextAttempt} attempts (~{nextAttempt * _retryIntervalSeconds / 3600}h).",
                            cancellationToken);
                    }
                    else
                    {
                        var retryAt = DateTime.UtcNow.AddSeconds(_retryIntervalSeconds);
                        booking.MarkAttemptFailed("Still pending", retryAt, DateTime.UtcNow);
                        await bookingRepository.SaveAsync(booking, cancellationToken);

                        logger.LogInformation(
                            "PplBookingReconciliation: booking still pending. OrderId={OrderId}, ReferenceId={ReferenceId}, Attempt={Attempt}/{Max}, NextRetryAt={NextRetryAt}",
                            booking.OrderId, booking.ReferenceId, nextAttempt, _maxAttempts, retryAt);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            var retryAt = DateTime.UtcNow.AddSeconds(_retryIntervalSeconds);
            booking.MarkAttemptFailed(ex.Message, retryAt, DateTime.UtcNow);
            await bookingRepository.SaveAsync(booking, cancellationToken);

            logger.LogWarning(
                ex,
                "PplBookingReconciliation: poll error. OrderId={OrderId}, ReferenceId={ReferenceId}, Attempt={Attempt}",
                booking.OrderId, booking.ReferenceId, booking.AttemptCount);
        }
    }

    private async Task EscalateAsync(
        PplPendingBooking booking,
        IPplPendingBookingRepository bookingRepository,
        IIncidentReporter incidentReporter,
        string issue,
        CancellationToken cancellationToken)
    {
        booking.MarkExhausted(issue, DateTime.UtcNow);
        await bookingRepository.SaveAsync(booking, cancellationToken);

        await incidentReporter.CreateInterventionTicketAsync(
            new InterventionTicket(
                OrderId: booking.OrderId,
                RefundId: null,
                Issue: issue,
                SuggestedAction:
                    "Order is Completed but has no tracking number assigned. " +
                    "Contact PPL to check booking status. " +
                    "Either re-book the shipment manually or initiate a refund for the customer."),
            cancellationToken);

        logger.LogError(
            "PplBookingReconciliation: booking escalated to manual intervention. OrderId={OrderId}, ReferenceId={ReferenceId}, Issue={Issue}",
            booking.OrderId, booking.ReferenceId, issue);
    }
}
