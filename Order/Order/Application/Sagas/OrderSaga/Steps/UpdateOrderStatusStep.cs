using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Steps;
using Domain.Exceptions;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public class UpdateOrderStatusStep(
    IInventoryGateway inventoryGateway,
    IOrderPersistenceService orderPersistenceService,
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

            if (context.PaymentStatus != OrderSagaPaymentStatus.Succeeded)
            {
                return new Fail(
                    $"Payment is not confirmed as succeeded. Current status: {context.PaymentStatus}");
            }

            if (string.IsNullOrEmpty(context.PaymentId))
                return new Fail("Payment ID not found in saga context");

            if (string.IsNullOrEmpty(context.ReservationId))
                return new Fail("Inventory reservation ID not found in saga context");

            logger.LogInformation(
                "Updating order {OrderId} status to Paid",
                data.CorrelationId);
            
            await orderPersistenceService.UpdateOrderAsync(
                data.CorrelationId,
                order =>
                {
                    var paymentId = PaymentId.From(context.PaymentId);
                    order.Pay(paymentId);
                    return Task.CompletedTask;
                },
                cancellationToken);

            logger.LogInformation(
                "Confirming inventory reservation {ReservationId} for order {OrderId}",
                context.ReservationId,
                data.CorrelationId);

            await inventoryGateway.ConfirmReservationAsync(
                context.ReservationId,
                cancellationToken);

            context.OrderStatusUpdated = true;

            logger.LogInformation(
                "Successfully updated order {OrderId} to Paid status",
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["OrderId"] = data.CorrelationId,
                ["Status"] = "Paid"
            });
        }
        catch (OrderNotFoundException ex)
        { 
            //return StepResult.Failure($"Order {data.CorrelationId} not found");
            // We return a failure that tells the Saga to stop and compensate.
            return new Fail($"Critical Error: {ex.Message}");
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