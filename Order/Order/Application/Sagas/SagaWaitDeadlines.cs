namespace Application.Sagas;

public static class SagaWaitDeadlines
{
    public static readonly TimeSpan InventoryReservationTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PaymentPush = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PaymentUncertain = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ReturnShipment = TimeSpan.FromDays(21);
}
