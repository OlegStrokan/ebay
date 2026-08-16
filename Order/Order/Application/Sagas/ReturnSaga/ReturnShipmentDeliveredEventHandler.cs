using Application.DTOs;
using Application.Sagas.Handlers.SagaContinuation;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga;

public class ReturnShipmentDeliveredEventHandler
: SagaContinuationEventHandler<ReturnShipmentDeliveredEventDto, ReturnSagaData, ReturnSagaContext>
{
    public override string EventType => "ReturnShipmentDeliveredEvent";
    public override string SagaType => "ReturnSaga";

    protected override string ResumeAtStepName => ReturnSagaSteps.ConfirmReturnReceived;

    public ReturnShipmentDeliveredEventHandler(
        IReturnSaga saga,
        ISagaRepository sagaRepository,
        ISagaDistributedLock distributedLock,
        ILogger<ReturnShipmentDeliveredEventHandler> logger) : base(saga, sagaRepository, distributedLock, logger)
    {
        
    }

    protected override Guid ExtractCorrelationId(ReturnShipmentDeliveredEventDto eventDto)
    {
        return eventDto.OrderId;
    }

    protected override void UpdateContextFromEvent(ReturnShipmentDeliveredEventDto eventDto, ReturnSagaContext context)
    {
        context.ReturnReceivedAt = eventDto.DeliveredAt;
        context.ReturnShipmentId = eventDto.ShipmentId;

        if (!string.IsNullOrEmpty(eventDto.TrackingNumber))
        {
            context.TrackingId = eventDto.TrackingNumber;
        }
    }
}