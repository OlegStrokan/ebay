using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public sealed class AwaitPaymentConfirmationStep(
    ILogger<AwaitPaymentConfirmationStep> logger)
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => "AwaitPaymentConfirmation";
    public int Order => 3;

    public Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        /* Backward compatibility for in-flight saga contexts that predate PaymentStatus.
        -----------
        if (context.PaymentStatus == OrderSagaPaymentStatus.NotStarted && !string.IsNullOrEmpty(context.PaymentId))
        {
            context.PaymentStatus = OrderSagaPaymentStatus.Succeeded;
        }
        --------
        * we dont need it, because we ca redeploy our service and no in flight snapshots exists,
        * but we we have rolling deploy without purguing saga state we definitely need in specific use-case:
        * user place order, saga created and goes throught steps, then saga wait for payment be approved
        * in external service so saga paused. then we deploy ned code where context should have PaymentStatus,
        * but saga which is paused didn't have it, so after deploy, after kafka receive payment successfull 
        * state, we need to check if context have paymentId, because in old code it's meant what payment was successfull,
        * but in new code we also should have context.paymentStatus which succesfful, but old saga dont have, so we force to have it
        */

        return Task.FromResult<StepOutcome>(context.PaymentStatus switch
        {
            OrderSagaPaymentStatus.Authorized => HandleAuthorized(data, context),
            OrderSagaPaymentStatus.Succeeded => HandleSucceeded(data, context),
            OrderSagaPaymentStatus.Failed => HandleFailed(data, context),
            OrderSagaPaymentStatus.Pending or OrderSagaPaymentStatus.RequiresAction or OrderSagaPaymentStatus.Uncertain => HandleWaiting(data, context),
            _ => new Fail("Payment state is unknown. Cannot continue order saga."),
        });
    }

    private StepOutcome HandleAuthorized(OrderSagaData data, OrderSagaContext context)
    {
       logger.LogInformation(
            "Authorization confirmed for order {OrderId}. Proceeding to fulfillment (capture happens later).",
            data.CorrelationId);

        return new Completed(new Dictionary<string, object>
        {
            ["ProviderPaymentIntentId"] = context.ProviderPaymentIntentId ?? data.PaymentIntentId ?? string.Empty,
            ["Status"] = context.PaymentStatus.ToString(),
        });
    }

    private StepOutcome HandleSucceeded(OrderSagaData data, OrderSagaContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PaymentId))
        {
            return new Fail("Payment was marked as succeeded but PaymentId is missing.");
        }

        logger.LogInformation(
            "Payment confirmation received for order {OrderId}. Continuing saga.",
            data.CorrelationId);

        return new Completed(new Dictionary<string, object>
        {
            ["PaymentId"] = context.PaymentId,
            ["Status"] = context.PaymentStatus.ToString(),
        });
    }

    private StepOutcome HandleFailed(OrderSagaData data, OrderSagaContext context)
    {
        var reason = context.PaymentFailureMessage ?? "Payment failed callback received";

        logger.LogWarning(
            "Payment failed for order {OrderId}. Triggering compensation. Reason: {Reason}",
            data.CorrelationId,
            reason);

        return new Fail($"Payment failed: {reason}");
    }

    private StepOutcome HandleWaiting(OrderSagaData data, OrderSagaContext context)
    {
        var statusDetail = context.PaymentStatus == OrderSagaPaymentStatus.Uncertain
            ? "Payment result uncertain due to timeout - waiting for webhook/reconciliation to resolve"
            : $"Waiting for payment finalization from provider";

        logger.LogInformation(
            "Order {OrderId} is still waiting for payment finalization. Status: {Status}. {Detail}",
            data.CorrelationId,
            context.PaymentStatus,
            statusDetail);

        return new WaitForEvent();
    }

    public Task CompensateAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
