
using Application.Interfaces;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga;

public sealed class OrderSaga(
    ISagaRepository sagaRepository,
    IEnumerable<ISagaStep<OrderSagaData, OrderSagaContext>> steps,
    ISagaErrorClassifier errorClassifier,
    ILogger<OrderSaga> logger,
    IFailedCompensationRetryRepository failedCompensationRetryRepository)
    : SagaBase<OrderSagaData, OrderSagaContext>(sagaRepository, steps, errorClassifier, logger, failedCompensationRetryRepository), IOrderSaga
{
    protected override string SagaType => "OrderSaga";
}