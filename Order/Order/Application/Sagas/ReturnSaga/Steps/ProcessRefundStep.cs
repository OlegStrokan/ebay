using Application.Common.Enums;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Steps;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga.Steps;

public class ProcessRefundStep(
    IPaymentGateway paymentGateway,
    IOrderPersistenceService orderPersistenceService,
    IReturnRequestPersistenceService returnRequestPersistenceService,
    IIncidentReporter incidentReporter,
    ILogger<ProcessRefundStep> logger
    ) : ISagaStep<ReturnSagaData, ReturnSagaContext>
{
    public string StepName => ReturnSagaSteps.ProcessRefund;
    public int Order => 4;

    public async Task<StepOutcome> ExecuteAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken)
    {
        try
        {

            if (!string.IsNullOrEmpty(context.RefundId))
            {
                logger.LogInformation(
                    "Refund already processed with {RefundId}. Skipping.",
                    context.RefundId);

                return new Completed(new Dictionary<string, object>
                {
                    ["RefundId"] = context.RefundId,
                });
            }
            
            logger.LogInformation(
                "Processing refund for order {OrderId}, amount {Amount} {Current}",
                data.CorrelationId,
                data.RefundAmount,
                data.Currency);
            
            var order = await orderPersistenceService.LoadOrderAsync(
                data.CorrelationId, cancellationToken);

            if (order == null)
            {
                return new Fail($"Order {data.CorrelationId} not found");
            }
            
            if (order.PaymentId == null)
                return new Fail("Order has no payment ID - cannot process refund");

            
            logger.LogInformation(
                "Processing refund for order {OrderId}, amount {Amount} {Currency}, original payment {PaymentId}",
                data.CorrelationId,
                data.RefundAmount,
                data.Currency,
                order.PaymentId);
            
            var refundId = await paymentGateway.RefundAsync(
                paymentId: order.PaymentId,
                amount: data.RefundAmount,
                currency: data.Currency,
                reason: $"Return request: {data.ReturnReason}",
                cancellationToken);

            context.RefundId = refundId;

            logger.LogInformation(
                "Refund processed successfully. Refund ID: {RefundId}",
                refundId);

            await returnRequestPersistenceService.UpdateReturnRequestAsync(
                data.CorrelationId,
                returnRequest =>
                {
                    if (returnRequest.Status != ReturnStatus.Received)
                        throw new InvalidOperationException(
                            $"ReturnRequest is in unexpected status {returnRequest.Status}. Expected: Received");

                    returnRequest.ProcessRefund(refundId);
                    return Task.CompletedTask;
                },
                cancellationToken);

            logger.LogInformation(
                "Refund {RefundId} processed and saved for order {OrderId}",
                refundId,
                data.CorrelationId);

            return new Completed(new Dictionary<string, object>
            {
                ["RefundId"] = refundId,
                ["OriginalPaymentId"] = order.PaymentId.ToString(),
                ["Amount"] = data.RefundAmount,
                ["Currency"] = data.Currency,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to process refund for order {OrderId}",
                data.CorrelationId);

            return new Fail($"Refund processing failed: {ex.Message}");
        }
    }

    public async Task CompensateAsync(
        ReturnSagaData data,
        ReturnSagaContext context,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(context.RefundId))
        {
            logger.LogInformation(
                "No refund to reverse for order {OrderId}",
                data.CorrelationId);
            return;
        }

        try
        {
            logger.LogCritical(
                "CRITICAL: Refund compensation triggered for order {OrderId}, refund {RefundId}. " +
                "Refund has been issued to customer but return saga failed. " +
                "Customer has money AND potentially keeping the product. " +
                "IMMEDIATE ACTION REQUIRED: Review order and determine if re-charge is needed.",
                data.CorrelationId,
                context.RefundId);

            await incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "RefundCompensationRequired",
                    OrderId: data.CorrelationId,
                    RefundId: context.RefundId,
                    Message: "Refund issued but return saga failed - manual review required",
                    Severity: AlertSeverity.Critical),
                cancellationToken);

            await incidentReporter.CreateInterventionTicketAsync(
                new InterventionTicket(
                    OrderId: data.CorrelationId,
                    RefundId: context.RefundId,
                    Issue: "Refund issued but downstream steps failed",
                    SuggestedAction: "1. Verify if customer returned items\n" +
                                     "2. If items not returned, contact customer\n" +
                                     "3. If customer unresponsive, initiate re-charge or collection"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to send alert during refund compensation for {RefundId}. " +
                "CRITICAL: Manual review required!",
                context.RefundId);
        }
    }
    
}