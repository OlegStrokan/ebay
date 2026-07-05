namespace Application.Sagas.OrderSaga;

public sealed class OrderSagaContext : SagaContext
{
    // external services IDs
    public string? ReservationId { get; set; }
    public string? PaymentId { get; set; }
    public string? ProviderPaymentIntentId { get; set; }
    public string? PaymentClientSecret { get; set; }
    public OrderSagaPaymentStatus PaymentStatus { get; set; } = OrderSagaPaymentStatus.NotStarted;
    public string? PaymentFailureCode { get; set; }
    public string? PaymentFailureMessage { get; set; }
    public string? ShipmentId { get; set; }
    public string? TrackingNumber { get; set; }
    
    // internal flags - for idempotency
    public bool ReservationConfirmed { get; set; }
    public bool OrderStatusUpdated { get; set; }
    public bool OrderCompleted { get; set; }
}