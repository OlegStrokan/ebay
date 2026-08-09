using Application.DTOs;
using Application.Sagas.Handlers.SagaContinuation;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga;

public sealed class PaymentFailedEventHandler
    : SagaContinuationEventHandler<PaymentFailedEventDto, OrderSagaData, OrderSagaContext>
{
    public override string EventType => "PaymentFailedEvent";
    public override string SagaType => "OrderSaga";

    protected override string ResumeAtStepName => OrderSagaSteps.AwaitPaymentConfirmation;

    public PaymentFailedEventHandler(
        IOrderSaga saga,
        ISagaRepository sagaRepository,
        ISagaDistributedLock distributedLock,
        ILogger<PaymentFailedEventHandler> logger)
        : base(saga, sagaRepository, distributedLock, logger)
    {
    }

    protected override Guid ExtractCorrelationId(PaymentFailedEventDto eventDto)
    {
        return Guid.TryParse(eventDto.OrderId, out var orderId)
            ? orderId
            : Guid.Empty;
    }

    protected override void UpdateContextFromEvent(PaymentFailedEventDto eventDto, OrderSagaContext context)
    {
        if (!string.IsNullOrWhiteSpace(eventDto.PaymentId))
        {
            context.PaymentId = eventDto.PaymentId;
        }

        context.ProviderPaymentIntentId = eventDto.ProviderPaymentIntentId;
        context.PaymentStatus = OrderSagaPaymentStatus.Failed;
        context.PaymentFailureCode = eventDto.ErrorCode;
        context.PaymentFailureMessage = eventDto.ErrorMessage;
    }
}
