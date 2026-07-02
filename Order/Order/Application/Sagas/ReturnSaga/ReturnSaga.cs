using Application.Interfaces;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.ReturnSaga;

public sealed class ReturnSaga(
    ISagaRepository sagaRepository,
    IEnumerable<ISagaStep<ReturnSagaData, ReturnSagaContext>> steps,
    ISagaErrorClassifier errorClassifier,
    ILogger<ReturnSaga> logger,
    IFailedCompensationRetryRepository failedCompensationRetryRepository)
    : SagaBase<ReturnSagaData, ReturnSagaContext>(sagaRepository, steps, errorClassifier, logger, failedCompensationRetryRepository), IReturnSaga
{
    protected override string SagaType => "ReturnSaga";
}