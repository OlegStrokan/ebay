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
    public string StepName => "ProcessPayment";
    public int Order => 2;

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
                    "Payment is still awaiting provider confirmation for order {OrderId}. " +
                    "Current status: {Status}",
                    data.CorrelationId,
                    context.PaymentStatus);

                return new WaitForEvent();
            }

            // B2C card path
            if (!string.IsNullOrEmpty(data.PaymentIntentId))
            {
                return await ExecuteCaptureAsync(data, context, cancellationToken);
            }

            // B2B / COD / recurring path: backend initiates the charge
            return await ExecuteBackendInitiatedPaymentAsync(data, context, cancellationToken);
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
        logger.LogInformation(
            "Capturing pre-authorized payment for order {OrderId}, PaymentIntentId {PaymentIntentId}",
            data.CorrelationId,
            data.PaymentIntentId);

        var captureResult = await paymentGateway.CaptureAsync(
            orderId: data.CorrelationId,
            customerId: data.CustomerId,
            providerPaymentIntentId: data.PaymentIntentId!,
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

    private async Task<StepOutcome> ExecuteBackendInitiatedPaymentAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Processing backend-initiated payment for order {OrderId}, amount {Amount} {Currency}",
            data.CorrelationId,
            data.TotalAmount,
            data.Currency);

        var paymentResult = await paymentGateway.ProcessPaymentAsync(
            orderId: data.CorrelationId,
            customerId: data.CustomerId,
            amount: data.TotalAmount,
            currency: data.Currency,
            paymentMethod: data.PaymentMethod.ToString(),
            cancellationToken);

        context.PaymentId = paymentResult.PaymentId;
        context.ProviderPaymentIntentId = paymentResult.ProviderPaymentIntentId;
        context.PaymentFailureCode = paymentResult.ErrorCode;
        context.PaymentFailureMessage = paymentResult.ErrorMessage;

        // oh, I am glad that you have better idea, you can stick your idea into your åss
        switch (paymentResult.Status)
        {
            case PaymentProcessingStatus.Succeeded:
                context.PaymentStatus = OrderSagaPaymentStatus.Succeeded;
                logger.LogInformation(
                    "Successfully processed payment {PaymentId} for order {OrderId}",
                    context.PaymentId,
                    data.CorrelationId);

                return new Completed(new Dictionary<string, object>
                {
                    ["PaymentId"] = context.PaymentId ?? string.Empty,
                    ["Amount"] = data.TotalAmount,
                    ["Currency"] = data.Currency,
                    ["Status"] = context.PaymentStatus.ToString(),
                });

            case PaymentProcessingStatus.Pending:
                context.PaymentStatus = OrderSagaPaymentStatus.Pending;
                logger.LogInformation(
                    "Payment {PaymentId} for order {OrderId} is pending provider confirmation",
                    context.PaymentId,
                    data.CorrelationId);
                return new WaitForEvent();

            case PaymentProcessingStatus.RequiresAction:
                context.PaymentStatus = OrderSagaPaymentStatus.RequiresAction;
                context.PaymentClientSecret = paymentResult.ClientSecret;
                logger.LogInformation(
                    "Payment {PaymentId} for order {OrderId} requires customer action before completion",
                    context.PaymentId,
                    data.CorrelationId);
                return new WaitForEvent();

            case PaymentProcessingStatus.Failed:
                context.PaymentStatus = OrderSagaPaymentStatus.Failed;
                var failedMessage = paymentResult.ErrorMessage ?? "Provider returned failed status";
                logger.LogWarning("Payment failed for order {OrderId}. Error: {Error}", data.CorrelationId, failedMessage);
                return new Fail($"Payment failed: {failedMessage}");

            default:
                context.PaymentStatus = OrderSagaPaymentStatus.Failed;
                return new Fail("Payment returned unknown processing status");
        }
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

        if (string.IsNullOrEmpty(context.PaymentId))
        {
            logger.LogInformation(
                "No payment to refund for order {OrderId}",
                data.CorrelationId
                );
            return;
        }

        // Skip refund if payment is Uncertain - it might succeed later via webhook/reconciliation
        // Skip if Failed - we can't refund what wasn't charged
        if (context.PaymentStatus is OrderSagaPaymentStatus.Uncertain or OrderSagaPaymentStatus.Failed)
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