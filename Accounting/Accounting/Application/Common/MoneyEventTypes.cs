namespace Application.Common;

public static class MoneyEventTypes
{
    public const string PaymentAuthorized = "PaymentAuthorizedEvent";

    public const string PaymentVoided = "PaymentVoidedEvent";

    public const string PaymentCaptured = "PaymentCapturedEvent";

    public const string RefundIssued = "RefundIssuedEvent";
}
