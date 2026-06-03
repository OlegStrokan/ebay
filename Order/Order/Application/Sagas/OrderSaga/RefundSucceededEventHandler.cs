using System.Text.Json;
using Application.DTOs;
using Application.Interfaces;
using Application.Sagas.Handlers;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga;

public sealed class RefundSucceededEventHandler(
    ICompensationRefundRetryRepository compensationRefundRetryRepository,
    ILogger<RefundSucceededEventHandler> logger)
    : ISagaEventHandler
{
    public string EventType => "RefundSucceededEvent";
    public string SagaType => "OrderSaga";

    public async Task HandleAsync(string eventPayload, CancellationToken cancellationToken)
    {
        RefundSucceededEventDto? eventDto;

        try
        {
            eventDto = JsonSerializer.Deserialize<RefundSucceededEventDto>(eventPayload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize RefundSucceededEvent. Invalid JSON format.");
            return;
        }

        if (eventDto is null || string.IsNullOrWhiteSpace(eventDto.OrderId))
        {
            logger.LogWarning("RefundSucceededEvent has null or missing OrderId. Skipping.");
            return;
        }

        if (!Guid.TryParse(eventDto.OrderId, out var orderId))
        {
            logger.LogWarning("RefundSucceededEvent has invalid OrderId {OrderId}. Skipping.", eventDto.OrderId);
            return;
        }

        logger.LogInformation(
            "Received RefundSucceededEvent for order {OrderId}, payment {PaymentId}, refund {RefundId}",
            eventDto.OrderId,
            eventDto.PaymentId,
            eventDto.RefundId);

        // Mark any pending compensation refund retry as completed.
        if (!string.IsNullOrWhiteSpace(eventDto.PaymentId))
        {
            var pendingRetry = await compensationRefundRetryRepository.GetPendingByOrderAndPaymentAsync(
                orderId,
                eventDto.PaymentId,
                cancellationToken);

            if (pendingRetry is not null)
            {
                pendingRetry.MarkCompleted(DateTime.UtcNow);
                await compensationRefundRetryRepository.SaveAsync(pendingRetry, cancellationToken);

                logger.LogInformation(
                    "Marked compensation refund retry {RetryId} as completed for order {OrderId}, payment {PaymentId}",
                    pendingRetry.Id,
                    orderId,
                    eventDto.PaymentId);
            }
        }
    }
}
