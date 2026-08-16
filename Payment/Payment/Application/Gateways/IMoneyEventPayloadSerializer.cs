using Domain.Entities;

namespace Application.Gateways;

public interface IMoneyEventPayloadSerializer
{
    string SerializePaymentAuthorized(string eventId, Payment payment, DateTime occurredAt);

    string SerializePaymentVoided(string eventId, Payment payment, DateTime occurredAt);

    string SerializePaymentCaptured(string eventId, Payment payment, DateTime occurredAt);

    string SerializeRefundIssued(string eventId, Payment payment, Refund refund, DateTime occurredAt);
}
