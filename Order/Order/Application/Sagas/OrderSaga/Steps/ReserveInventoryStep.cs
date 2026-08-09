using Application.Common.Enums;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public sealed class ReserveInventoryStep(
    IInventoryGateway inventoryGateway,
    IIncidentReporter incidentReporter,
    ILogger<ReserveInventoryStep> logger)
    : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => OrderSagaSteps.ReserveInventory;
    public int Order => 1;

    public async Task<StepOutcome> ExecuteAsync(OrderSagaData data,
        OrderSagaContext context, CancellationToken cancellationToken)
    {
        try
        {
            /* if you have a question "is this reliable way to check idempotency?" same for other steps
             * I will say fuck yea. sagaContext is persisted to db after every step
             * on crash/restart ResumeFromStepAsync deserialized the context and context.<field_name>
             * will be populated. crash between reserveAsync and the context save would re-run the step
             * but inventoryGateway.ReserveAsync is idempotent on the service side so we don't care
             * -----------------------------------------------------------------------------------------
             * BUT: it's idempotent because this is our microservice which developed by the best developer
             * in the world - me. If the inventory service were an external service developed by another
             * team which is stupid enough to write in public api what idempotency is supported, but in
             * reality they were middle-level coders relied on claude 4.5 so there is no fucking idempotency
             * support, we will have unpredictable behavior. Should we recheck idempotency on our side?
             * It's cost money, if we Jeff Bezos creation we would use a deduplication table on our side.
             * If we RogaIKopyta we don't do it, because we try to sue external system to make us fool
             */
            if (!string.IsNullOrEmpty(context.ReservationId))
            {
                logger.LogInformation(
                    "Inventory already reserved with {ReservationId}. Skipping.",
                    context.ReservationId);
        
                return new Completed(new Dictionary<string, object>
                {
                    ["ReservationId"] = context.ReservationId,
                });
            }
            logger.LogInformation(
                "Reserving inventory for order {OrderId} with {ItemCount} items",
                data.CorrelationId,
                data.Items.Count);
            
            var reservationId = await inventoryGateway.ReserveAsync(
                orderId: data.CorrelationId,
                items: data.Items,
                cancellationToken);

            context.ReservationId = reservationId;

            logger.LogInformation(
                "Successfully reserved inventory {ReservationId} for order {OrderId}",
                reservationId,
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["ReservationId"] = reservationId,
                ["ItemsReversed"] = data.Items.Count
            });
        }
        catch (InsufficientInventoryException ex)
        {
            logger.LogWarning(
                ex, "Insufficient inventory for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Insufficient inventory: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to reserve inventory for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Inventory reservation failed: {ex.Message}");
        }
    } 

    public async Task CompensateAsync(OrderSagaData data, 
        OrderSagaContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.ReservationId))
        {
            logger.LogInformation(
                "No reservation to release for order {OrderId}",
                data.CorrelationId);
            return;
        }

        try
        {
            logger.LogInformation("Releasing inventory reservation {ReservationId} for order {OrderId}",
                context.ReservationId,
                data.CorrelationId);

            await inventoryGateway.ReleaseReservationAsync(context.ReservationId, cancellationToken);
            
            logger.LogInformation("Successfully released reservation {ReservationId}", context.ReservationId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to release inventory reservation {ReservationId}. Manual intervention may be required",
                context.ReservationId);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Issue: $"Failed to release inventory reservation {context.ReservationId}",
                    SuggestedAction: "Manually release the reservation in inventory service"),
                cancellationToken);
            // don't throw - we want to continue compensating other steps
        }
    }
}
