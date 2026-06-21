namespace Domain.Enums;

public enum PaymentStatus
{
    Created = 0,
    PendingProviderConfirmation = 1,
    Succeeded = 2,
    Failed = 3,
    RefundPending = 4,
    Refunded = 5,
    RefundFailed = 6,
    Authorized = 7,
}