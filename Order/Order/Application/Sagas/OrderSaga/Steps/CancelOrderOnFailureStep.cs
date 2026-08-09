using Application.Common.Enums;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Steps;
using Domain.Exceptions;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

// Compensate cancels the order, ensuring cancellation happens regardless of which downstream step fails
public sealed class CancelOrderOnFailureStep(
    IOrderPersistenceService orderPersistenceService,
    IPaymentGateway paymentGateway,
    IIncidentReporter incidentReporter,
    ILogger<CancelOrderOnFailureStep> logger)
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => OrderSagaSteps.CancelOrderOnFailure;
    public int Order => 0;

    public Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
        => Task.FromResult<StepOutcome>(new Completed());

    public async Task CompensateAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await orderPersistenceService.LoadOrderAsync(
                data.CorrelationId,
                cancellationToken);

            if (order is null)
            {
                logger.LogWarning(
                    "Compensate skipped: Order {OrderId} not found. It may have never been created.",
                    data.CorrelationId);
                return;
            }

            if (order.Status == OrderStatus.Cancelled)
            {
                logger.LogInformation(
                    "Order {OrderId} is already cancelled. Skipping cancellation.",
                    data.CorrelationId);
                return;
            }

            if (order.Status == OrderStatus.Completed)
            {
                var issue = $"Saga compensation requested but order {data.CorrelationId} is already Completed.";
                logger.LogCritical("{Issue}", issue);
                await ReportCriticalInterventionAsync(
                    data.CorrelationId,
                    issue,
                    "Investigate terminal-state workflow. Order completion is already committed.",
                    cancellationToken);
                return;
            }

            if (!order.Status.CanTransitionTo(OrderStatus.Cancelled))
            {
                var issue =
                    $"Saga compensation blocked: cannot transition order {data.CorrelationId} from {order.Status} to Cancelled.";
                logger.LogCritical("{Issue}", issue);
                await ReportCriticalInterventionAsync(
                    data.CorrelationId,
                    issue,
                    "Manually reconcile order state and compensation side effects.",
                    cancellationToken);
                return;
            }

            logger.LogInformation("Cancelling order {OrderId} due to saga compensation", data.CorrelationId);

            await orderPersistenceService.UpdateOrderAsync(
                data.CorrelationId,
                order =>
                {
                    order.Cancel(["Saga compensation - order creation failed"]);
                    return Task.CompletedTask;
                },
                cancellationToken);

            logger.LogInformation("Order {OrderId} cancelled successfully", data.CorrelationId);

            // If the frontend pre-authorized but capture never happened, release the hold
            if (!string.IsNullOrEmpty(data.PaymentIntentId) && string.IsNullOrEmpty(context.PaymentId))
            {
                await TryCancelAuthorizationAsync(data.CorrelationId, data.PaymentIntentId, cancellationToken);
            }
        }
        catch (OrderNotFoundException)
        {
            logger.LogWarning(
                "Compensate skipped: Order {OrderId} not found. It may have never been created.",
                data.CorrelationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel order {OrderId} during compensation", data.CorrelationId);

            await ReportCriticalInterventionAsync(
                data.CorrelationId,
                $"Order cancellation during saga compensation failed: {ex.Message}",
                "Manually cancel the order and reconcile payment/inventory/shipment side effects.",
                cancellationToken);
        }
    }

    private async Task TryCancelAuthorizationAsync(
        Guid orderId,
        string providerPaymentIntentId,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Cancelling pre-authorized payment intent {PaymentIntentId} for order {OrderId}",
                providerPaymentIntentId,
                orderId);

            await paymentGateway.CancelAuthorizationAsync(providerPaymentIntentId, cancellationToken);

            logger.LogInformation(
                "Successfully cancelled authorization for order {OrderId}",
                orderId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to cancel authorization {PaymentIntentId} for order {OrderId}. Manual intervention required.",
                providerPaymentIntentId,
                orderId);

            await ReportCriticalInterventionAsync(
                orderId,
                $"Failed to cancel Stripe authorization {providerPaymentIntentId} during saga compensation: {ex.Message}",
                "Manually cancel the PaymentIntent in Stripe dashboard to release the customer's authorization hold.",
                cancellationToken);
        }
    }

    private async Task ReportCriticalInterventionAsync(
        Guid orderId,
        string issue,
        string suggestedAction,
        CancellationToken cancellationToken)
    {
        try
        {
            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "OrderCompensationCancellationFailure",
                    OrderId: orderId,
                    RefundId: null,
                    Message: issue,
                    Severity: AlertSeverity.Critical),
                cancellationToken);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: orderId,
                    RefundId: null,
                    Issue: issue,
                    SuggestedAction: suggestedAction),
                cancellationToken);
        }
        catch (Exception reportEx)
        {
            logger.LogError(
                reportEx,
                "Failed to create critical incident report for order {OrderId}",
                orderId);
        }
    }
}
