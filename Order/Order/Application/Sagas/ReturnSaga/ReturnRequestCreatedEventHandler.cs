using Application.DTOs;
using Application.Sagas.Handlers.SagaCreation;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga;

public class ReturnRequestCreatedEventHandler
: SagaEventHandler<ReturnRequestCreatedEventDto, ReturnSagaData, ReturnSagaContext>
{
    public override string EventType => "ReturnRequestCreatedEvent";
    public override string SagaType => "ReturnSaga";

    public ReturnRequestCreatedEventHandler(
        IReturnSaga saga,
        ISagaRepository sagaRepository,
        ILogger<ReturnRequestCreatedEventHandler> logger)
        : base(saga, sagaRepository, logger)
    { }

    protected override ReturnSagaData MapEventToSagaData(ReturnRequestCreatedEventDto eventDto)
    {
        return new ReturnSagaData
        {
            CorrelationId = eventDto.OrderId,
            ReturnRequestId = eventDto.ReturnRequestId,
            CustomerId = eventDto.CustomerId,
            ReturnReason = eventDto.Reason,
            ReturnedItems = eventDto.ItemsToReturn,
            RefundAmount = eventDto.RefundAmount,
            Currency = eventDto.Currency
        };
    }
}