using Application.Common.Enums;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Steps;
using Domain.Exceptions;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga.Steps;

public sealed class ConfirmReturnReceivedStep(
    IReturnRequestPersistenceService returnRequestPersistenceService,
    IIncidentReporter incidentReporter,
    ILogger<ConfirmReturnReceivedStep> logger
    ) : ISagaStep<ReturnSagaData, ReturnSagaContext>
{
    public string StepName => ReturnSagaSteps.ConfirmReturnReceived;
    public int Order => 3;

    public async Task<StepOutcome> ExecuteAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Confirming return for order {OrderId}, shipment {ShipmentId}",
                data.CorrelationId,
                context.ReturnShipmentId);

            await returnRequestPersistenceService.UpdateReturnRequestAsync(
                data.CorrelationId,
                returnRequest =>
                {
                    if (returnRequest.Status == ReturnStatus.Received)
                    {
                        logger.LogInformation(
                            "ReturnRequest {ReturnRequestId} already marked as received (duplicate webhook)",
                            returnRequest.Id.Value);
                        return Task.CompletedTask;
                    }

                    if (returnRequest.Status != ReturnStatus.Pending)
                    {
                        throw new InvalidOperationException(
                            $"ReturnRequest in unexpected status: {returnRequest.Status}");
                    }

                    returnRequest.MarkAsReceived();
                    return Task.CompletedTask;
                }, cancellationToken);

            var receivedAt = DateTime.UtcNow;
            context.ReturnReceivedAt = receivedAt;

            logger.LogInformation(
                "Return confirmed for order {OrderId}. Package physically received at warehouse.",
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["ReturnShipmentId"] = context.ReturnShipmentId ?? "N/A",
                ["ReceivedAt"] = receivedAt,
                ["Status"] = "ReturnReceived"
            });
        }
        catch (OrderNotFoundException ex)
        {
            logger.LogInformation(
                ex,
                "ReturnRequest for order {OrderId} not found ",
                data.CorrelationId);
            return new Fail($"ReturnRequest for order {data.CorrelationId} not found");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to confirm return receipt for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Failed to confirm return: {ex.Message}");
        }
    }       

    public async Task CompensateAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogWarning(
                "Compensating ConfirmReturnReceived for order {OrderId}",
                data.CorrelationId);


            // Note: In event-sourced systems, you can't easily "unreceive" items.
            logger.LogWarning(
                "ReturnRequest {ReturnRequestId} was marked as received but saga failed. " +
                "Manual review required to determine if items were actually returned.",
                data.CorrelationId);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: data.CorrelationId,
                    RefundId: null,
                    Issue: "ReturnRequest marked as Received but saga failed - items may have been returned",
                    SuggestedAction: "Verify warehouse receipt and determine if refund should proceed manually"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to compensate ConfirmReturnReceived for order {OrderId}. Manual intervention required!",
                data.CorrelationId);
        }
    }
}





















