using System.Text.Json;
using Application.Common.Enums;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Handlers;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga;

public sealed class RefundFailedEventHandler(
    ICompensationRefundRetryRepository compensationRefundRetryRepository,
    IIncidentReporter incidentReporter,
    ILogger<RefundFailedEventHandler> logger)
    : ISagaEventHandler
{
    public string EventType => "RefundFailedEvent";
    public string SagaType => "OrderSaga";

    public async Task HandleAsync(string eventPayload, CancellationToken cancellationToken)
    {
        RefundFailedEventDto? eventDto;

        try
        {
            eventDto = JsonSerializer.Deserialize<RefundFailedEventDto>(eventPayload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize RefundFailedEvent. Invalid JSON format.");
            return;
        }

        if (eventDto is null || string.IsNullOrWhiteSpace(eventDto.OrderId))
        {
            logger.LogWarning("RefundFailedEvent has null or missing OrderId. Skipping.");
            return;
        }

        if (!Guid.TryParse(eventDto.OrderId, out var orderId))
        {
            logger.LogWarning("RefundFailedEvent has invalid OrderId {OrderId}. Skipping.", eventDto.OrderId);
            return;
        }

        logger.LogWarning(
            "Received RefundFailedEvent for order {OrderId}, payment {PaymentId}, refund {RefundId}. Error: {ErrorCode} - {ErrorMessage}",
            eventDto.OrderId,
            eventDto.PaymentId,
            eventDto.RefundId,
            eventDto.ErrorCode,
            eventDto.ErrorMessage);

        if (string.IsNullOrWhiteSpace(eventDto.PaymentId))
        {
            logger.LogWarning("RefundFailedEvent missing PaymentId for order {OrderId}. Cannot enqueue retry.", eventDto.OrderId);
            return;
        }

        // Check if there's already a pending retry for this order+payment.
        var existingRetry = await compensationRefundRetryRepository.GetPendingByOrderAndPaymentAsync(
            orderId,
            eventDto.PaymentId,
            cancellationToken);

        if (existingRetry is not null)
        {
            logger.LogInformation(
                "Compensation refund retry {RetryId} already pending for order {OrderId}, payment {PaymentId}. Refund failure will be handled by retry worker.",
                existingRetry.Id,
                orderId,
                eventDto.PaymentId);
            return;
        }

        // No existing retry — enqueue one so the retry worker can attempt the refund again.
        await compensationRefundRetryRepository.EnqueueIfNotExistsAsync(
            orderId,
            eventDto.PaymentId,
            amount: 0, // Amount unknown from event — retry worker will use original compensation amount
            currency: string.Empty,
            reason: $"Refund failed via event: {eventDto.ErrorCode} - {eventDto.ErrorMessage}",
            cancellationToken);

        await incidentReporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "RefundFailed",
                OrderId: orderId,
                RefundId: eventDto.RefundId,
                Message: $"Refund failed for payment {eventDto.PaymentId}. Error: {eventDto.ErrorCode} - {eventDto.ErrorMessage}. A retry has been enqueued.",
                Severity: AlertSeverity.Critical),
            cancellationToken);

        logger.LogWarning(
            "Enqueued compensation refund retry for order {OrderId}, payment {PaymentId} after RefundFailedEvent",
            orderId,
            eventDto.PaymentId);
    }
}
