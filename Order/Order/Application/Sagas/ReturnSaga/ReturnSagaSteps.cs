namespace Application.Sagas.ReturnSaga;

public static class ReturnSagaSteps
{
    public const string ValidateReturnRequest = "ValidateReturnRequest";
    public const string AwaitReturnShipment = "AwaitReturnShipment";
    public const string ConfirmReturnReceived = "ConfirmReturnReceived";
    public const string ProcessRefund = "ProcessRefund";
    public const string UpdateAccountingRecords = "UpdateAccountingRecords";
    public const string CompleteReturn = "CompleteReturn";
}
