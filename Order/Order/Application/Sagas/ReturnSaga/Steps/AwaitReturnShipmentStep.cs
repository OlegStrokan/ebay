using Application.Common.Enums;
using Application.Gateways;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga.Steps;

public sealed class AwaitReturnShipmentStep(
    IShippingGateway shippingGateway,
    IShippingWebhookUrlProvider shippingWebhookUrlProvider,
    IIncidentReporter incidentReporter,
    ILogger<AwaitReturnShipmentStep> logger
    ) : ISagaStep<ReturnSagaData, ReturnSagaContext>
{
    public string StepName => "AwaitReturnShipment";
    public int Order => 2;

    public async Task<StepOutcome> ExecuteAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Initiating return shipment for order {OrderId}",
                data.CorrelationId);

            string returnShipmentId;
            
            if (string.IsNullOrEmpty(context.ReturnShipmentId))
            {
            
                var shipmentResult = await shippingGateway.CreateReturnShipmentAsync(
                    orderId: data.CorrelationId,
                    customerId: data.CustomerId,
                    items: data.ReturnedItems,
                    carrier: data.ShippingCarrier,
                    cancellationToken
                );

                returnShipmentId = shipmentResult.ReturnShipmentId;
                context.ReturnShipmentId = returnShipmentId;
                logger.LogInformation("Created return shipment {Id}", returnShipmentId);

            }
            else
            {
                returnShipmentId = context.ReturnShipmentId;
                logger.LogInformation("Shipment {Id} already exists. Skipping creation.", returnShipmentId);
            }
            
            
            logger.LogInformation(
                "Return shipment created {ShipmentId} for order {OrderId}. Awaiting delivery...",
                returnShipmentId,
                data.CorrelationId);

            var callbackUrl = BuildReturnDeliveredCallbackUrl(
                shippingWebhookUrlProvider.GetReturnDeliveredCallbackUrl(),
                data.CorrelationId,
                returnShipmentId);
            
            await shippingGateway.RegisterWebhookAsync(
                shipmentId: returnShipmentId,
                callbackUrl: callbackUrl,
                events: ["return.delivered"],
                cancellationToken);

            return new WaitForEvent();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to process return shipment for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Return shipment failed: {ex.Message}");
        }
    }

    public async Task CompensateAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.ReturnShipmentId))
        {
            logger.LogInformation(
                "No return shipment to cancel for order {OrderId}",
                data.CorrelationId);
            return;
        }

        try
        {
            logger.LogInformation(
                "Cancelling return shipment {ShipmentId} for order {OrderId}",
                context.ReturnShipmentId,
                data.CorrelationId);

            await shippingGateway.CancelReturnShipmentAsync(
                returnShipmentId: context.ReturnShipmentId,
                reason: "Return saga compensation - return request cancelled",
                cancellationToken);

            logger.LogInformation(
                "Successfully cancelled return shipment {ShipmentId}",
                context.ReturnShipmentId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to cancel return shipment {ShipmentId}. Manual intervention required",
                context.ReturnShipmentId);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Issue: $"Failed to cancel return shipment {context.ReturnShipmentId} during compensation",
                    SuggestedAction: "Manually cancel return shipment with shipping provider"),
                cancellationToken);
        }

    }

    private static string BuildReturnDeliveredCallbackUrl(string baseUrl, Guid orderId, string shipmentId)
    {
        var normalizedBaseUrl = baseUrl.Trim();
        var separator = normalizedBaseUrl.Contains('?') ? '&' : '?';

        return string.Concat(
            normalizedBaseUrl,
            separator,
            "orderId=", Uri.EscapeDataString(orderId.ToString()),
            "&shipmentId=", Uri.EscapeDataString(shipmentId));
    }
}