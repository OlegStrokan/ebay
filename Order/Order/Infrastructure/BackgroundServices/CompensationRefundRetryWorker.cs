using Application.Common.Enums;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Models;

namespace Infrastructure.BackgroundServices;

public sealed class CompensationRefundRetryWorker(
    IServiceProvider serviceProvider,
    ILogger<CompensationRefundRetryWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _batchSize = configuration.GetValue<int>("CompensationRefundRetry:BatchSize", 20);
    private readonly int _maxRetries = configuration.GetValue<int>("CompensationRefundRetry:MaxRetries", 3);
    private readonly int _pollIntervalSeconds = configuration.GetValue<int>("CompensationRefundRetry:PollIntervalSeconds", 30);
    private readonly int _baseRetryDelaySeconds = configuration.GetValue<int>("CompensationRefundRetry:BaseRetryDelaySeconds", 30);
    private readonly int _maxRetryDelaySeconds = configuration.GetValue<int>("CompensationRefundRetry:MaxRetryDelaySeconds", 900);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        logger.LogInformation(
            "CompensationRefundRetryWorker started. BatchSize={BatchSize}, MaxRetries={MaxRetries}, PollIntervalSeconds={PollIntervalSeconds}",
            _batchSize,
            _maxRetries,
            _pollIntervalSeconds);

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
                logger.LogError(ex, "Unexpected error in CompensationRefundRetryWorker loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("CompensationRefundRetryWorker stopped");
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();

        var retryRepository = scope.ServiceProvider.GetRequiredService<ICompensationRefundRetryRepository>();
        var paymentGateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var incidentReporter = scope.ServiceProvider.GetRequiredService<IIncidentReporter>();

        var now = DateTime.UtcNow;
        var dueRetries = await retryRepository.ClaimDuePendingAsync(now, _batchSize, cancellationToken);

        if (dueRetries.Count == 0)
        {
            return;
        }

        foreach (var retry in dueRetries)
        {
            await ProcessRetryAsync(retry, retryRepository, paymentGateway, incidentReporter, cancellationToken);
        }
    }

    private async Task ProcessRetryAsync(
        CompensationRefundRetry retry,
        ICompensationRefundRetryRepository retryRepository,
        IPaymentGateway paymentGateway,
        IIncidentReporter incidentReporter,
        CancellationToken cancellationToken)
    {
        try
        {
            var refundResult = await paymentGateway.RefundWithStatusAsync(
                retry.PaymentId,
                retry.Amount,
                retry.Currency,
                retry.Reason,
                cancellationToken);

            if (refundResult.Status == RefundProcessingStatus.Pending)
            {
                var now = DateTime.UtcNow;
                var nextAttemptAt = now.Add(CalculateRetryDelay(Math.Max(1, retry.RetryCount + 1)));
                retry.MarkPendingVerification(nextAttemptAt, now);
                await retryRepository.SaveAsync(retry, cancellationToken);

                logger.LogWarning(
                    "Compensation refund retry is still pending provider confirmation. RetryId={RetryId}, OrderId={OrderId}, PaymentId={PaymentId}, RefundId={RefundId}, NextAttemptAt={NextAttemptAt}",
                    retry.Id,
                    retry.OrderId,
                    retry.PaymentId,
                    refundResult.RefundId,
                    retry.NextAttemptAtUtc);

                return;
            }

            retry.MarkCompleted(DateTime.UtcNow);
            await retryRepository.SaveAsync(retry, cancellationToken);

            logger.LogInformation(
                "Compensation refund retry succeeded. RetryId={RetryId}, OrderId={OrderId}, PaymentId={PaymentId}, RefundId={RefundId}",
                retry.Id,
                retry.OrderId,
                retry.PaymentId,
                refundResult.RefundId);
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            var nextAttemptNumber = retry.RetryCount + 1;

            if (IsRetriable(ex) && nextAttemptNumber < _maxRetries)
            {
                var delay = CalculateRetryDelay(nextAttemptNumber);
                retry.MarkAttemptFailed(ex.Message, now.Add(delay), now);
                await retryRepository.SaveAsync(retry, cancellationToken);

                logger.LogWarning(
                    ex,
                    "Compensation refund retry failed with retriable error. RetryId={RetryId}, OrderId={OrderId}, PaymentId={PaymentId}, Attempt={Attempt}/{MaxRetries}, NextAttemptAt={NextAttemptAt}",
                    retry.Id,
                    retry.OrderId,
                    retry.PaymentId,
                    nextAttemptNumber,
                    _maxRetries,
                    retry.NextAttemptAtUtc);

                return;
            }

            retry.MarkExhausted(ex.Message, now);
            await retryRepository.SaveAsync(retry, cancellationToken);

            logger.LogError(
                ex,
                "Compensation refund retry exhausted or non-retriable. RetryId={RetryId}, OrderId={OrderId}, PaymentId={PaymentId}, Attempts={Attempts}",
                retry.Id,
                retry.OrderId,
                retry.PaymentId,
                retry.RetryCount);

            await SendManualInterventionAlertAsync(retry, ex, incidentReporter, cancellationToken);
        }
    }

    private async Task SendManualInterventionAlertAsync(
        CompensationRefundRetry retry,
        Exception ex,
        IIncidentReporter incidentReporter,
        CancellationToken cancellationToken)
    {
        try
        {
            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "PaymentRefundCompensationRetryExhausted",
                    OrderId: retry.OrderId,
                    RefundId: null,
                    Message: $"Compensation refund retry exhausted for payment {retry.PaymentId}. LastError={ex.Message}",
                    Severity: AlertSeverity.Critical),
                cancellationToken);
        }
        catch (Exception alertException)
        {
            logger.LogError(
                alertException,
                "Failed to send retry exhaustion incident alert for order {OrderId}, payment {PaymentId}",
                retry.OrderId,
                retry.PaymentId);
        }
    }

    private TimeSpan CalculateRetryDelay(int attemptNumber)
    {
        var safeAttempt = Math.Max(1, attemptNumber);
        var growth = Math.Pow(2, safeAttempt - 1);
        var seconds = Math.Min(_maxRetryDelaySeconds, _baseRetryDelaySeconds * growth);
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsRetriable(Exception ex)
    {
        if (ex is GatewayUnavailableException)
        {
            return true;
        }

        if (ex is TimeoutException || ex is HttpRequestException || ex is TaskCanceledException)
        {
            return true;
        }

        return ex.InnerException is not null && IsRetriable(ex.InnerException);
    }
}
