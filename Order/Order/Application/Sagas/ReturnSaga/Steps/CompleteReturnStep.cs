using Application.Common.Enums;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga.Steps;

public sealed class CompleteReturnStep(
    IReturnRequestPersistenceService returnRequestPersistenceService,
    IIncidentReporter incidentReporter,
    ILogger<CompleteReturnStep> logger)
: ISagaStep<ReturnSagaData, ReturnSagaContext>
{
    public string StepName => ReturnSagaSteps.CompleteReturn;
    public int Order => 6;

    public async Task<StepOutcome> ExecuteAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken)
    {

        try
        {
            logger.LogInformation(
                "Completing return process for order {OrderId}",
                data.CorrelationId);

            await returnRequestPersistenceService.UpdateReturnRequestAsync(
                data.CorrelationId,
                returnRequest =>
                {
                    returnRequest.Complete();
                    return Task.CompletedTask;
                }, cancellationToken);
            
            logger.LogInformation(
                "Return completed successfully for order {OrderId}. Final status: Returned",
                data.CorrelationId);
            

            return new Completed(new Dictionary<string, object>
            {
                ["OrderId"] = data.CorrelationId,
                ["FinalStatus"] = "Returned",
                ["RefundId"] = context.RefundId ?? "N/A",
                ["RefundAmount"] = data.RefundAmount
            });
        }
        
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to complete return for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Return completion failed: {ex.Message}");
        }
    }

    public async Task CompensateAsync(
        ReturnSagaData data, 
        ReturnSagaContext context, 
        CancellationToken cancellationToken)
    {
       // final step compensation
       
       logger.LogWarning(
           "Compensation triggered on CompleteReturn step for order {OrderId}. " +
           "ReturnRequest was marked as Completed but saga failed. " +
           "Manual review required - possible inconsistent state.",
           data.CorrelationId);

       await incidentReporter.CreateInterventionTicketAsync(
           new InterventionTicket(
               OrderId: data.CorrelationId,
               RefundId: context.RefundId,
               Issue: "ReturnRequest marked as Completed but saga failed after",
               SuggestedAction: "Verify final return state and reconcile if needed"),
           cancellationToken);

    }
}