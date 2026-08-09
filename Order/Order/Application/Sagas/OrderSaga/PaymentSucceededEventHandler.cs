using System.Text.Json;
using Application.Common.Enums;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Application.Models;
using Application.Sagas.Handlers.SagaContinuation;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga;

public sealed class PaymentSucceededEventHandler
    : SagaContinuationEventHandler<PaymentSucceededEventDto, OrderSagaData, OrderSagaContext>
{
    private readonly ICompensationRefundRetryRepository _compensationRefundRetryRepository;
    private readonly IIncidentReporter _incidentReporter;

    public override string EventType => "PaymentSucceededEvent";
    public override string SagaType => "OrderSaga";

    protected override string ResumeAtStepName => OrderSagaSteps.AwaitPaymentConfirmation;

    public PaymentSucceededEventHandler(
        IOrderSaga saga,
        ISagaRepository sagaRepository,
        ISagaDistributedLock distributedLock,
        ICompensationRefundRetryRepository compensationRefundRetryRepository,
        IIncidentReporter incidentReporter,
        ILogger<PaymentSucceededEventHandler> logger)
        : base(saga, sagaRepository, distributedLock, logger)
    {
        _compensationRefundRetryRepository = compensationRefundRetryRepository;
        _incidentReporter = incidentReporter;
    }

    protected override Guid ExtractCorrelationId(PaymentSucceededEventDto eventDto)
    {
        return Guid.TryParse(eventDto.OrderId, out var orderId)
            ? orderId
            : Guid.Empty;
    }

    protected override void UpdateContextFromEvent(PaymentSucceededEventDto eventDto, OrderSagaContext context)
    {
        if (!string.IsNullOrWhiteSpace(eventDto.PaymentId))
        {
            context.PaymentId = eventDto.PaymentId;
        }

        context.ProviderPaymentIntentId = eventDto.ProviderPaymentIntentId;
        context.PaymentStatus = OrderSagaPaymentStatus.Succeeded;
        context.PaymentFailureCode = null;
        context.PaymentFailureMessage = null;
    }

    // Called when the saga is already Compensating/Compensated — meaning the saga timed out
    // and released inventory before Stripe confirmed the capture. The customer has been charged
    // but no refund was enqueued during compensation (PaymentStatus was Pending at that point).
    // Enqueue a CompensationRefundRetry row so the worker issues the refund automatically.
    protected override async Task HandleCompensatedLateEventAsync(
        SagaState sagaState,
        PaymentSucceededEventDto eventDto,
        CancellationToken cancellationToken)
    {
        var orderId = Guid.TryParse(eventDto.OrderId, out var id) ? id : Guid.Empty;

        if (string.IsNullOrWhiteSpace(eventDto.PaymentId))
        {
            await _incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "LatePaymentSucceededNoPaymentId",
                    OrderId: orderId,
                    RefundId: null,
                    Message: $"PaymentSucceededEvent arrived after saga compensation for order {eventDto.OrderId} but carries no PaymentId. Manual refund required.",
                    Severity: AlertSeverity.Critical),
                cancellationToken);
            return;
        }

        OrderSagaData? sagaData = null;
        try
        {
            sagaData = JsonSerializer.Deserialize<OrderSagaData>(sagaState.Payload);
        }
        catch (JsonException)
        {
            // fall through to the null check below
        }

        if (sagaData == null)
        {
            await _incidentReporter.SendAlertAsync(
                new IncidentAlert(
                    AlertType: "LatePaymentSucceededDeserializeFailure",
                    OrderId: orderId,
                    RefundId: null,
                    Message: $"PaymentSucceededEvent arrived after saga compensation for order {eventDto.OrderId} but saga payload could not be deserialized. Manual refund required.",
                    Severity: AlertSeverity.Critical),
                cancellationToken);
            return;
        }

        await _compensationRefundRetryRepository.EnqueueIfNotExistsAsync(
            orderId: sagaData.CorrelationId,
            paymentId: eventDto.PaymentId,
            amount: sagaData.TotalAmount,
            currency: sagaData.Currency,
            reason: "Late PaymentSucceededEvent after saga compensation - auto-refund",
            cancellationToken);

        await _incidentReporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "LatePaymentSucceededAfterCompensation",
                OrderId: sagaData.CorrelationId,
                RefundId: null,
                Message: $"PaymentSucceededEvent arrived after saga compensation for order {sagaData.CorrelationId}, payment {eventDto.PaymentId}. Refund has been auto-enqueued via CompensationRefundRetryRepository.",
                Severity: AlertSeverity.Critical),
            cancellationToken);
    }
}
