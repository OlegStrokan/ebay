using Application.Interfaces;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public sealed class CompleteOrderStep(
    IOrderPersistenceService orderPersistenceService,
    ILogger<CompleteOrderStep> logger)
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => "CompleteOrder";
    public int Order => 7;

    public async Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.OrderCompleted)
            {
                logger.LogInformation(
                    "Order {OrderId} already approved and completed, skipping",
                    data.CorrelationId);
                return new Completed();
            }

            logger.LogInformation("Approving and completing order {OrderId}", data.CorrelationId);

            await orderPersistenceService.UpdateOrderAsync(
                data.CorrelationId,
                order =>
                {
                    order.Approve();
                    order.Complete();
                    return Task.CompletedTask;
                },
                cancellationToken);

            context.OrderCompleted = true;

            logger.LogInformation(
                "Successfully approved and completed order {OrderId}",
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["OrderId"] = data.CorrelationId,
                ["Status"] = "Completed"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete order {OrderId}", data.CorrelationId);
            return new Fail($"Failed to complete order: {ex.Message}");
        }
    }

    public Task CompensateAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken)
    {
        // Order completion is final — no meaningful compensation at this point
        logger.LogWarning(
            "CompleteOrderStep compensation is a no-op for order {OrderId}",
            data.CorrelationId);
        return Task.CompletedTask;
    }
}
