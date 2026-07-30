using Application.Gateways;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public class UpdateOrderStatusStep(
    IInventoryGateway inventoryGateway,
    ILogger<UpdateOrderStatusStep> logger
    )
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => "UpdateOrderStatus";
    public int Order => 4;

    public async Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {

            if (context.OrderStatusUpdated)
            {
                logger.LogInformation(
                    "Order {OrderId} status already updated, skipping",
                    data.CorrelationId);
                return new Completed();
            }

            // At step 4 the payment is authorized (hold placed) but not yet captured —
            // money moves at step 6. Requiring Succeeded here would always fail the
            // frontend/capture-late path, and order.Pay() belongs after actual capture.
            if (context.PaymentStatus is not (OrderSagaPaymentStatus.Authorized or OrderSagaPaymentStatus.Succeeded))
            {
                return new Fail(
                    $"Payment must be authorized before order status can be updated. Current status: {context.PaymentStatus}");
            }

            if (string.IsNullOrEmpty(context.ReservationId))
                return new Fail("Inventory reservation ID not found in saga context");

            if (!context.ReservationConfirmed)
            {
                logger.LogInformation(
                    "Confirming inventory reservation {ReservationId} for order {OrderId}",
                    context.ReservationId,
                    data.CorrelationId);

                await inventoryGateway.ConfirmReservationAsync(
                    context.ReservationId,
                    cancellationToken);

                context.ReservationConfirmed = true;
            }

            context.OrderStatusUpdated = true;

            logger.LogInformation(
                "Order {OrderId} inventory confirmed; payment will be recorded after capture",
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["OrderId"] = data.CorrelationId,
                ["InventoryConfirmed"] = true
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to update order {OrderId} status",
                data.CorrelationId);

            return new Fail($"Failed to update order status: {ex.Message}");
        }
    }

    public Task CompensateAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
        => Task.CompletedTask; // Order cancellation is handled by CancelOrderOnFailureStep (Order: 0)
}