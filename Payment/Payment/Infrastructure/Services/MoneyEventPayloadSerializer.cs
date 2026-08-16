using Application.Common;
using Application.Gateways;
using Domain.Entities;
using System.Text.Json;

namespace Infrastructure.Services;

internal sealed class MoneyEventPayloadSerializer : IMoneyEventPayloadSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Payment holds neither a provider fee nor a tax split yet, so both legs post as zero
    // until pricing supplies real values. The ledger keeps the fields to avoid a contract change.
    private const decimal UnknownFee = 0m;
    private const decimal UnknownTax = 0m;

    public string SerializePaymentAuthorized(string eventId, Payment payment, DateTime occurredAt) =>
        Serialize(eventId, MoneyEventTypes.PaymentAuthorized, payment, refundId: null, payment.Amount.Amount, occurredAt);

    public string SerializePaymentVoided(string eventId, Payment payment, DateTime occurredAt) =>
        Serialize(eventId, MoneyEventTypes.PaymentVoided, payment, refundId: null, payment.Amount.Amount, occurredAt);

    public string SerializePaymentCaptured(string eventId, Payment payment, DateTime occurredAt) =>
        Serialize(eventId, MoneyEventTypes.PaymentCaptured, payment, refundId: null, payment.Amount.Amount, occurredAt);

    public string SerializeRefundIssued(string eventId, Payment payment, Refund refund, DateTime occurredAt) =>
        Serialize(eventId, MoneyEventTypes.RefundIssued, payment, refund.Id.Value, refund.Amount.Amount, occurredAt);

    private static string Serialize(
        string eventId,
        string eventType,
        Payment payment,
        string? refundId,
        decimal amount,
        DateTime occurredAt)
    {
        var payload = new MoneyEventPayload(
            EventId: eventId,
            EventType: eventType,
            PaymentId: payment.Id.Value,
            OrderId: payment.OrderId,
            RefundId: refundId,
            ProviderPaymentIntentId: payment.ProviderPaymentIntentId?.Value,
            Amount: amount,
            Currency: payment.Amount.Currency,
            Fee: UnknownFee,
            Tax: UnknownTax,
            OccurredAt: occurredAt);

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private sealed record MoneyEventPayload(
        string EventId,
        string EventType,
        string PaymentId,
        string OrderId,
        string? RefundId,
        string? ProviderPaymentIntentId,
        decimal Amount,
        string Currency,
        decimal Fee,
        decimal Tax,
        DateTime OccurredAt);
}
