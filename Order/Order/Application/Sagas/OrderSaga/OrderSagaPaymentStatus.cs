namespace Application.Sagas.OrderSaga;

public enum OrderSagaPaymentStatus
{
    NotStarted = 0,
    Pending = 1,
    RequiresAction = 2,
    Succeeded = 3,
    Failed = 4,
    Uncertain = 5,
    Authorized = 6,
}
