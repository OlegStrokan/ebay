namespace Application.Sagas.OrderSaga;

public static class OrderSagaSteps
{
    public const string ReserveInventory = "ReserveInventory";
    public const string AuthorizePayment = "AuthorizePayment";
    public const string AwaitPaymentConfirmation = "AwaitPaymentConfirmation";
    public const string CreateShipment = "CreateShipment";
    public const string UpdateOrderStatus = "UpdateOrderStatus";
    public const string CapturePayment = "CapturePayment";
    public const string SendConfirmationEmail = "SendConfirmationEmail";
    public const string CompleteOrder = "CompleteOrder";
    public const string CancelOrderOnFailure = "CancelOrderOnFailure";
}
