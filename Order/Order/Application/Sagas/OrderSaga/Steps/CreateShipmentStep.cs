using System.Text.Json;
using Application.Common.Enums;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Sagas.Steps;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public sealed class CreateShipmentStep(
    IShippingGateway shippingGateway,
    IOrderPersistenceService orderPersistenceService,
    IPplPendingBookingRepository pplPendingBookingRepository,
    IIncidentReporter incidentReporter,
    ILogger<CreateShipmentStep> logger
    ) : ISagaStep<OrderSagaData, OrderSagaContext>
{
    public string StepName => "CreateShipment";
    public int Order => 5;
    
    public async Task<StepOutcome> ExecuteAsync(OrderSagaData data, OrderSagaContext context, CancellationToken cancellationToken)
    {
        try
        {

            if (!string.IsNullOrEmpty(context.ShipmentId))
            {
                logger.LogInformation(
                    "Shipment already reserved with {ShipmentId}. Skipping.",
                    context.ShipmentId);

                return new Completed(new Dictionary<string, object>
                {
                    ["ShipmentId"] = context.ShipmentId,
                });
            }

            logger.LogInformation(
                "Creating shipment for order {OrderId}",
                data.CorrelationId);
            
            var (shipmentId, trackingNumber) = await shippingGateway.CreateShipmentAsync(
                orderId: data.CorrelationId,
                deliveryAddress: data.DeliveryAddress,
                items: data.Items,
                carrier: data.ShippingCarrier,
                cancellationToken);

            context.ShipmentId = shipmentId;
            context.TrackingNumber = trackingNumber;

            logger.LogInformation(
                "Tracking number retrieved: {TrackingNumber}",
                trackingNumber);

            await orderPersistenceService.UpdateOrderAsync(
                data.CorrelationId,
                order =>
                {
                    var trackingId = TrackingId.From(trackingNumber);
                    order.AssignTracking(trackingId);
                    return Task.CompletedTask;
                },
                cancellationToken);

            logger.LogInformation(
                "Successfully created shipment {ShipmentId} with tracking {TrackingNumber} for order {OrderId}",
                shipmentId,
                trackingNumber,
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["ShipmentId"] = shipmentId,
                ["DeliveryAddress"] = $"{data.DeliveryAddress.Street}, {data.DeliveryAddress.City}"
            });
        }
        catch (OrderNotFoundException ex)
        {
            logger.LogInformation(
                ex,
                "Order with ID {OrderId} not found ",
                data.CorrelationId);
                return new Fail($"Order {data.CorrelationId} not found");
        }
        catch (InvalidAddressException ex)
        {
            logger.LogInformation(
                ex,
                "Invalid delivery address for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Invalid delivery address: {ex.Message}");
        }
        catch (PplBookingPendingException ex)
        {
            // PPL depot did not settle within the initial poll window.
            // Persist the reference for the background reconciliation worker and let the
            // saga continue — the order completes without a tracking number for now.
            await pplPendingBookingRepository.EnqueueAsync(
                data.CorrelationId, ex.ReferenceId, cancellationToken);

            logger.LogWarning(
                ex,
                "PPL booking did not settle within poll window for order {OrderId}. ReferenceId={ReferenceId}. Enqueued for reconciliation.",
                data.CorrelationId, ex.ReferenceId);

            return new Completed(new Dictionary<string, object>
            {
                ["ShipmentStatus"] = "PplPendingReconciliation",
                ["PplReferenceId"] = ex.ReferenceId,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CreateShipment failed for order {OrderId}: {Message}",
                data.CorrelationId, ex.Message);

            return new Fail($"CreateShipment failed: {ex.Message}");
        }
    }

    public async Task CompensateAsync(OrderSagaData data, OrderSagaContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.ShipmentId))
        {
            logger.LogInformation("No shipment to cancel for order {OrderId}", data.CorrelationId);
            return;
        }
        
        try
        {
            logger.LogInformation("Compensating shipment {ShipmentId} for order {OrderId}",
                context.ShipmentId,
                data.CorrelationId);

            try
            {
                
                await shippingGateway.CancelShipmentAsync(
                    shipmentId: context.ShipmentId,
                    cancellationToken);

                logger.LogInformation(
                    "Successfully cancelled shipment {ShipmentId}",
                    context.ShipmentId);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to cancel shipment {ShipmentId}. Manual cancellation may be required",
                    context.ShipmentId);
                // don't throw - continue to revert order tracking
            }

            await orderPersistenceService.UpdateOrderAsync(
                data.CorrelationId,
                order =>
                {
                    order.RevertTrackingAssignment();
                    return Task.CompletedTask;
                },
                cancellationToken);
            
            logger.LogInformation(
                "Successfully compensated shipment step for order {OrderId}",
                data.CorrelationId);
        }
        catch (OrderNotFoundException)
        {
            logger.LogWarning("Compensate skipped: Order {OrderId} not found. It may have never been created.", data.CorrelationId);
        }
        
        catch (Exception ex)
        {
            
            logger.LogError(
                ex,
                "Failed to compensate shipment {ShipmentId}. Manual intervention required",
                context.ShipmentId);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Issue: $"Failed to cancel shipment {context.ShipmentId} during compensation",
                    SuggestedAction: "Manually cancel the shipment with the shipping provider"),
                cancellationToken);
        }
    }
    }

