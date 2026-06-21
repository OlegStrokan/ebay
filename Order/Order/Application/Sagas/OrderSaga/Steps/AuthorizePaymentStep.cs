using Application.Common.Enums;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

/// <summary>
/// Two paths:
///  - Frontend pre-auth: the browser already created a manual-capture hold and
///    passed its <c>PaymentIntentId we just record it.
///  - Backend-initiated: we call the payment service to authorize (manual
///    capture) and obtain a provider payment intent id.
/// </summary>
public sealed class AuthorizePaymentStep(
    IPaymentGateway paymentGateway,
    IIncidentReporter incidentReporter,
    ILogger<AuthorizePaymentStep> logger)
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => "AuthorizePayment";
    public int Order => 2;

    public async Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        if (context.PaymentStatus is OrderSagaPaymentStatus.Authorized or OrderSagaPaymentStatus.Succeeded)
        {
            logger.LogInformation(
                "Payment already authorized/captured for order {OrderId}. Skipping authorization.",
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["ProviderPaymentIntentId"] = context.ProviderPaymentIntentId ?? data.PaymentIntentId ?? string.Empty,
                ["Status"] = context.PaymentStatus.ToString(),
            });
        }

        if (context.PaymentStatus == OrderSagaPaymentStatus.Failed)
        {
            var error = context.PaymentFailureMessage ?? "Authorization was marked as failed by callback";
            logger.LogWarning("Authorization already failed for order {OrderId}. Error: {Error}", data.CorrelationId, error);
            return new Fail($"Authorization failed: {error}");
        }

        try
        {
            // Frontend pre-auth: the hold already exists, created by the browser.
            // 3DS/SCA (the "tap your bank app" wait) happened client-side before
            // the order was submitted, so there is nothing to wait for here.
            if (!string.IsNullOrEmpty(data.PaymentIntentId))
            {
                context.ProviderPaymentIntentId = data.PaymentIntentId;
                context.PaymentStatus = OrderSagaPaymentStatus.Authorized;

                logger.LogInformation(
                    "Recorded frontend-created authorization {ProviderPaymentIntentId} for order {OrderId}",
                    data.PaymentIntentId,
                    data.CorrelationId);

                return new Completed(new Dictionary<string, object>
                {
                    ["ProviderPaymentIntentId"] = data.PaymentIntentId,
                    ["Status"] = context.PaymentStatus.ToString(),
                });
            }

            return await ExecuteBackendAuthorizationAsync(data, context, cancellationToken);
        }
        catch (PaymentDeclinedException ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "PAYMENT_DECLINED";
            context.PaymentFailureMessage = ex.Message;
            logger.LogWarning(ex, "Authorization declined for order {OrderId}", data.CorrelationId);
            return new Fail($"Authorization declined: {ex.Message}");
        }
        catch (InsufficientFundsException ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "INSUFFICIENT_FUNDS";
            context.PaymentFailureMessage = ex.Message;
            logger.LogWarning(ex, "Insufficient funds to authorize order {OrderId}", data.CorrelationId);
            return new Fail($"Insufficient funds: {ex.Message}");
        }
        catch (GatewayUnavailableException ex) when (ex.Reason == GatewayUnavailableReason.Timeout)
        {
            // The hold may or may not have been placed. Mark Uncertain so
            // compensation voids it if it exists.
            context.PaymentStatus = OrderSagaPaymentStatus.Uncertain;
            context.PaymentFailureCode = "AUTH_RESULT_UNCERTAIN";
            context.PaymentFailureMessage = ex.Message;
            logger.LogWarning(ex, "Authorization call timed out for order {OrderId}. Marking Uncertain.", data.CorrelationId);
            return new WaitForEvent();
        }
        catch (GatewayUnavailableException ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "PAYMENT_GATEWAY_UNAVAILABLE";
            context.PaymentFailureMessage = ex.Message;
            logger.LogError(ex, "Payment service unavailable to authorize order {OrderId}", data.CorrelationId);
            return new Fail($"Payment gateway unavailable: {ex.Message}");
        }
        catch (Exception ex)
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Failed;
            context.PaymentFailureCode = "AUTH_ERROR";
            context.PaymentFailureMessage = ex.Message;
            logger.LogError(ex, "Authorization failed for order {OrderId}", data.CorrelationId);
            return new Fail($"Authorization failed: {ex.Message}");
        }
    }

    private async Task<StepOutcome> ExecuteBackendAuthorizationAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Authorizing backend-initiated payment for order {OrderId}, amount {Amount} {Currency}",
            data.CorrelationId,
            data.TotalAmount,
            data.Currency);

        var result = await paymentGateway.AuthorizeAsync(
            orderId: data.CorrelationId,
            customerId: data.CustomerId,
            amount: data.TotalAmount,
            currency: data.Currency,
            paymentMethod: data.PaymentMethod.ToString(),
            cancellationToken);

        context.PaymentId = result.PaymentId;
        context.ProviderPaymentIntentId = result.ProviderPaymentIntentId;
        context.PaymentFailureCode = result.ErrorCode;
        context.PaymentFailureMessage = result.ErrorMessage;

        switch (result.Status)
        {
            case PaymentProcessingStatus.Authorized:
            case PaymentProcessingStatus.Succeeded: // some methods (e.g. invoice) settle immediately
                context.PaymentStatus = OrderSagaPaymentStatus.Authorized;
                logger.LogInformation(
                    "Authorization placed for order {OrderId}, PaymentId {PaymentId}",
                    data.CorrelationId,
                    context.PaymentId);
                return new Completed(new Dictionary<string, object>
                {
                    ["PaymentId"] = context.PaymentId ?? string.Empty,
                    ["ProviderPaymentIntentId"] = context.ProviderPaymentIntentId ?? string.Empty,
                    ["Status"] = context.PaymentStatus.ToString(),
                });

            case PaymentProcessingStatus.Pending:
            case PaymentProcessingStatus.RequiresAction:
                // Defensive: server-side 3DS is not used in this flow (frontend
                // handles it). Wait for provider confirmation if it ever occurs.
                context.PaymentStatus = OrderSagaPaymentStatus.Pending;
                context.PaymentClientSecret = result.ClientSecret;
                logger.LogInformation(
                    "Authorization for order {OrderId} is awaiting provider confirmation",
                    data.CorrelationId);
                return new WaitForEvent();

            case PaymentProcessingStatus.Failed:
            default:
                context.PaymentStatus = OrderSagaPaymentStatus.Failed;
                var failed = result.ErrorMessage ?? "Provider returned a non-authorized status";
                logger.LogWarning("Authorization failed for order {OrderId}. Error: {Error}", data.CorrelationId, failed);
                return new Fail($"Authorization failed: {failed}");
        }
    }

    public async Task CompensateAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        // Once captured, the hold is consumed — CapturePaymentStep owns the refund.
        if (context.PaymentStatus == OrderSagaPaymentStatus.Succeeded)
        {
            logger.LogInformation(
                "Payment already captured for order {OrderId}; authorization compensation is a no-op.",
                data.CorrelationId);
            return;
        }

        // Nothing was held.
        if (context.PaymentStatus is OrderSagaPaymentStatus.NotStarted or OrderSagaPaymentStatus.Failed)
        {
            return;
        }

        var providerPaymentIntentId = string.IsNullOrWhiteSpace(context.ProviderPaymentIntentId)
            ? data.PaymentIntentId
            : context.ProviderPaymentIntentId;

        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            logger.LogInformation("No authorization hold to void for order {OrderId}", data.CorrelationId);
            return;
        }

        try
        {
            await paymentGateway.CancelAuthorizationAsync(providerPaymentIntentId, cancellationToken);
            logger.LogInformation(
                "Voided authorization {ProviderPaymentIntentId} for order {OrderId} during compensation.",
                providerPaymentIntentId,
                data.CorrelationId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to void authorization {ProviderPaymentIntentId} for order {OrderId}. Manual reconciliation required.",
                providerPaymentIntentId,
                data.CorrelationId);

            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "AuthorizationVoidFailed",
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Message: $"Failed to void authorization {providerPaymentIntentId} during compensation.",
                    Severity: AlertSeverity.Critical),
                cancellationToken);
        }
    }
}
