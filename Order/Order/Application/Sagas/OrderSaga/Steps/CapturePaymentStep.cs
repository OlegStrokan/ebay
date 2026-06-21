using Application.Common.Enums;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public sealed class CapturePaymentStep(
    IPaymentGateway paymentGateway,
    ICompensationRefundRetryRepository compensationRefundRetryRepository,
    IIncidentReporter incidentReporter,
    ILogger<CapturePaymentStep> logger)
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => "CapturePayment";
    public int Order => 6;

    public async Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // Backward compatibility for snapshots saved before PaymentStatus existed.
            if (context.PaymentStatus == OrderSagaPaymentStatus.NotStarted && !string.IsNullOrEmpty(context.PaymentId))
            {
                context.PaymentStatus = OrderSagaPaymentStatus.Succeeded;
            }

            if (context.PaymentStatus == OrderSagaPaymentStatus.Succeeded && !string.IsNullOrEmpty(context.PaymentId))
            {
                logger.LogInformation(
                    "Payment already processed for order {OrderId} with PaymentId {PaymentId}. Skipping.",
                    data.CorrelationId,
                    context.PaymentId);

                return new Completed(new Dictionary<string, object>
                {
                    ["PaymentId"] = context.PaymentId,
                });
            }

            if (context.PaymentStatus == OrderSagaPaymentStatus.Failed)
            {
                var error = context.PaymentFailureMessage ?? "Payment was marked as failed by callback";
                logger.LogWarning(
                    "Payment already failed for order {OrderId}. Error: {Error}",
                    data.CorrelationId,
                    error);
                return new Fail($"Payment failed: {error}");
            }

            if (context.PaymentStatus is
                OrderSagaPaymentStatus.Pending or
                OrderSagaPaymentStatus.RequiresAction or
                OrderSagaPaymentStatus.Uncertain)
            {
                logger.LogInformation(
                    "Payment is still awaiting authorization confirmation for order {OrderId}. " +
                    "Current status: {Status}",
                    data.CorrelationId,
                    context.PaymentStatus);

                return new WaitForEvent();
            }

            // Capture the authorization hold placed earlier by AuthorizePaymentStep
            // (frontend-created or backend-initiated). This is where money moves.
            return await ExecuteCaptureAsync(data, context, cancellationToken);
        }
        catch (PaymentDeclinedException ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "PAYMENT_DECLINED";
            context.PaymentFailureMessage = ex.Message;

            logger.LogWarning(ex, "Payment declined for order {OrderId}", data.CorrelationId);
            return new Fail($"Payment declined: {ex.Message}");
        }
        catch (InsufficientFundsException ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "INSUFFICIENT_FUNDS";
            context.PaymentFailureMessage = ex.Message;

            logger.LogWarning(ex, "Insufficient funds for order {OrderId}", data.CorrelationId);
            return new Fail($"Insufficient funds: {ex.Message}");
        }
        catch (GatewayUnavailableException ex) when (ex.Reason == GatewayUnavailableReason.Timeout)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Uncertain;
            context.PaymentFailureCode = "PAYMENT_RESULT_UNCERTAIN";
            context.PaymentFailureMessage = ex.Message;

            logger.LogWarning(
                ex,
                "Payment call timed out for order {OrderId}. Marking as Uncertain and waiting for webhook/reconciliation",
                data.CorrelationId);

            return new WaitForEvent();
        }
        catch (GatewayUnavailableException ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "PAYMENT_GATEWAY_UNAVAILABLE";
            context.PaymentFailureMessage = ex.Message;

            logger.LogError(ex, "Payment service unavailable for order {OrderId}", data.CorrelationId);
            return new Fail($"Payment gateway unavailable: {ex.Message}");
        }
        catch (Exception ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "PAYMENT_ERROR";
            context.PaymentFailureMessage = ex.Message;

            logger.LogError(ex, "Payment processing failed for order {OrderId}", data.CorrelationId);
            return new Fail($"Payment failed: {ex.Message}");
        }
    }

    private async Task<StepOutcome> ExecuteCaptureAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        var providerPaymentIntentId = !string.IsNullOrWhiteSpace(context.ProviderPaymentIntentId)
            ? context.ProviderPaymentIntentId!
            : data.PaymentIntentId;

        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            logger.LogWarning("No authorization hold to capture for order {OrderId}", data.CorrelationId);
            return new Fail("No authorization hold available to capture.");
        }

        logger.LogInformation(
            "Capturing authorized payment for order {OrderId}, ProviderPaymentIntentId {ProviderPaymentIntentId}",
            data.CorrelationId,
            providerPaymentIntentId);

        var captureResult = await paymentGateway.CaptureAsync(
            orderId: data.CorrelationId,
            customerId: data.CustomerId,
            providerPaymentIntentId: providerPaymentIntentId,
            amount: data.TotalAmount,
            currency: data.Currency,
            cancellationToken);

        context.PaymentId = captureResult.PaymentId;
        context.ProviderPaymentIntentId = captureResult.ProviderPaymentIntentId;
        context.PaymentFailureCode = captureResult.ErrorCode;
        context.PaymentFailureMessage = captureResult.ErrorMessage;

        if (captureResult.Status == PaymentProcessingStatus.Succeeded)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Succeeded;

            logger.LogInformation(
                "Successfully captured payment {PaymentId} for order {OrderId}",
                context.PaymentId,
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["PaymentId"] = context.PaymentId ?? string.Empty,
                ["Amount"] = data.TotalAmount,
                ["Currency"] = data.Currency,
                ["Status"] = context.PaymentStatus.ToString(),
            });
        }

        context.PaymentStatus = OrderSagaPaymentStatus.Failed;
        var failedMessage = captureResult.ErrorMessage ?? "Capture returned non-succeeded status";
        logger.LogWarning("Payment capture failed for order {OrderId}. Error: {Error}", data.CorrelationId, failedMessage);
        return new Fail($"Payment capture failed: {failedMessage}");
    }

    public async Task CompensateAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        // @todo: we can delete it
        // Backward compatibility for snapshots saved before PaymentStatus existed.
        if (context.PaymentStatus == OrderSagaPaymentStatus.NotStarted && !string.IsNullOrEmpty(context.PaymentId))
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Succeeded;
        }

        if (context.PaymentStatus == OrderSagaPaymentStatus.Uncertain)
        {
            logger.LogWarning(
                "Payment status is Uncertain during compensation for order {OrderId}. Executing deferred safety path.",
                data.CorrelationId);

            if (!string.IsNullOrWhiteSpace(context.PaymentId))
            {
                await compensationRefundRetryRepository.EnqueueIfNotExistsAsync(
                    orderId: data.CorrelationId,
                    paymentId: context.PaymentId,
                    amount: data.TotalAmount,
                    currency: data.Currency,
                    reason: "Uncertain payment verification - saga compensation",
                    cancellationToken);

                logger.LogWarning(
                    "Enqueued uncertain-payment compensation verification for order {OrderId}, payment {PaymentId}.",
                    data.CorrelationId,
                    context.PaymentId);

                return;
            }

            var providerPaymentIntentId = string.IsNullOrWhiteSpace(context.ProviderPaymentIntentId)
                ? data.PaymentIntentId
                : context.ProviderPaymentIntentId;

            if (!string.IsNullOrWhiteSpace(providerPaymentIntentId))
            {
                try
                {
                    await paymentGateway.CancelAuthorizationAsync(providerPaymentIntentId, cancellationToken);

                    logger.LogInformation(
                        "Cancelled uncertain authorization {ProviderPaymentIntentId} for order {OrderId} during compensation.",
                        providerPaymentIntentId,
                        data.CorrelationId);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to cancel uncertain authorization {ProviderPaymentIntentId} for order {OrderId}. Manual reconciliation required.",
                        providerPaymentIntentId,
                        data.CorrelationId);

                    await incidentReporter.SendAlertAsync(
                        new IncidentAlert(
                            AlertType: "PaymentCompensationUncertain",
                            OrderId: data.CorrelationId,
                            RefundId: null,
                            Message: $"Uncertain payment compensation failed to cancel authorization {providerPaymentIntentId}.",
                            Severity: AlertSeverity.Critical),
                        cancellationToken);
                }

                return;
            }

            logger.LogError(
                "Uncertain payment compensation has no PaymentId and no PaymentIntentId for order {OrderId}. Manual reconciliation required.",
                data.CorrelationId);

            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "PaymentCompensationUncertain",
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Message: "Uncertain payment compensation has no identifiers for automated verification/refund.",
                    Severity: AlertSeverity.Critical),
                cancellationToken);

            return;
        }

        if (string.IsNullOrEmpty(context.PaymentId))
        {
            logger.LogInformation(
                "No payment to refund for order {OrderId}",
                data.CorrelationId
                );
            return;
        }

        // Skip if Failed - we can't refund what wasn't charged
        if (context.PaymentStatus == OrderSagaPaymentStatus.Failed)
        {
            logger.LogInformation(
                "Skipping refund for order {OrderId}. Payment status is {Status}.",
                data.CorrelationId,
                context.PaymentStatus);
            return;
        }

        if (context.PaymentStatus != OrderSagaPaymentStatus.Succeeded)
        {
            logger.LogInformation(
                "Skipping refund for order {OrderId}. Payment status is {Status}.",
                data.CorrelationId,
                context.PaymentStatus);
            return;
        }

        try
        {
            logger.LogInformation(
                "Refunding payment {PaymentId} for order {OrderId}",
                context.PaymentId,
                data.CorrelationId
            );

            var refundResult = await paymentGateway.RefundWithStatusAsync(
                paymentId: context.PaymentId,
                amount: data.TotalAmount,
                currency: data.Currency,
                reason: "Order cancelled - saga compensation",
                cancellationToken);

            if (refundResult.Status == RefundProcessingStatus.Pending)
            {
                logger.LogWarning(
                    "Refund {RefundId} for payment {PaymentId} is pending provider confirmation during compensation for order {OrderId}. Enqueueing follow-up verification.",
                    refundResult.RefundId,
                    context.PaymentId,
                    data.CorrelationId);

                // Enqueue a follow-up so the retry worker can verify the refund completes.
                // If the RefundSucceededEvent arrives from Payment before the worker picks this up,
                // the RefundSucceededEventHandler will mark it completed.
                await compensationRefundRetryRepository.EnqueueIfNotExistsAsync(
                    orderId: data.CorrelationId,
                    paymentId: context.PaymentId,
                    amount: data.TotalAmount,
                    currency: data.Currency,
                    reason: "Pending refund verification - saga compensation",
                    cancellationToken);
            }
            else
            {
                logger.LogInformation(
                    "Successfully refunded payment {PaymentId} with refund {RefundId}",
                    context.PaymentId,
                    refundResult.RefundId);
            }
        }
        catch (Exception ex)
        {
            if (IsRetriableRefundFailure(ex))
            {
                await compensationRefundRetryRepository.EnqueueIfNotExistsAsync(
                    orderId: data.CorrelationId,
                    paymentId: context.PaymentId,
                    amount: data.TotalAmount,
                    currency: data.Currency,
                    reason: "Order cancelled - saga compensation",
                    cancellationToken);

                logger.LogWarning(
                    ex,
                    "Refund compensation for payment {PaymentId} failed with retriable error. Retry has been enqueued for order {OrderId}",
                    context.PaymentId,
                    data.CorrelationId);

                return;
            }

            logger.LogError(
                ex,
                "Failed to refund payment {PaymentId}. Manual refund required!",
                context.PaymentId);

            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "PaymentRefundCompensationFailed",
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Message: $"Failed to refund payment {context.PaymentId} during saga compensation",
                    Severity: AlertSeverity.Critical),
                cancellationToken);
        }

        
    }

    private static bool IsRetriableRefundFailure(Exception ex)
    {
        if (ex is GatewayUnavailableException)
        {
            return true;
        }

        if (ex is TimeoutException || ex is HttpRequestException || ex is TaskCanceledException)
        {
            return true;
        }

        return ex.InnerException is not null && IsRetriableRefundFailure(ex.InnerException);
    }
}