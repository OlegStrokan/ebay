using Domain.Common;
using Domain.Enums;
using Domain.Exceptions;

namespace Domain.Entities;

// Outbox row for the accounting ledger. Written in the same transaction as the money mutation.
public sealed class OutboundMoneyEvent : Entity<Guid>
{
    private OutboundMoneyEvent()
    {
    }

    private OutboundMoneyEvent(
        Guid id,
        string eventId,
        string paymentId,
        string orderId,
        string eventType,
        string payloadJson,
        DateTime createdAt)
        : base(id)
    {
        EventId = eventId;
        PaymentId = paymentId;
        OrderId = orderId;
        EventType = eventType;
        PayloadJson = payloadJson;
        Status = CallbackDeliveryStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public string EventId { get; private set; } = string.Empty;

    public string PaymentId { get; private set; } = string.Empty;

    public string OrderId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = string.Empty;

    public CallbackDeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public DateTime? LastAttemptAt { get; private set; }

    public DateTime? NextRetryAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboundMoneyEvent Create(
        string eventId,
        string paymentId,
        string orderId,
        string eventType,
        string payloadJson,
        DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new InvalidValueException("Money event id cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(paymentId))
        {
            throw new InvalidValueException("Payment id cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            throw new InvalidValueException("Order id cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new InvalidValueException("Money event type cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidValueException("Money event payload cannot be empty");
        }

        var now = createdAt ?? DateTime.UtcNow;
        return new OutboundMoneyEvent(
            Guid.NewGuid(),
            eventId.Trim(),
            paymentId.Trim(),
            orderId.Trim(),
            eventType.Trim(),
            payloadJson,
            now);
    }

    public bool CanAttempt(DateTime now)
    {
        if (Status is CallbackDeliveryStatus.Delivered or CallbackDeliveryStatus.PermanentFailure)
        {
            return false;
        }

        return NextRetryAt is null || NextRetryAt <= now;
    }

    public void MarkDelivered(DateTime? deliveredAt = null)
    {
        var now = deliveredAt ?? DateTime.UtcNow;
        Status = CallbackDeliveryStatus.Delivered;
        LastAttemptAt = now;
        UpdatedAt = now;
        NextRetryAt = null;
        LastError = null;
        AttemptCount++;
    }

    public void MarkAttemptFailed(string error, DateTime nextRetryAt, DateTime? attemptedAt = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidValueException("Money event failure reason cannot be empty.");
        }

        var now = attemptedAt ?? DateTime.UtcNow;
        Status = CallbackDeliveryStatus.Failed;
        LastAttemptAt = now;
        UpdatedAt = now;
        NextRetryAt = nextRetryAt;
        LastError = error.Trim();
        AttemptCount++;
    }

    public void MarkPermanentFailure(string error, DateTime? attemptedAt = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidValueException("Money event failure reason cannot be empty.");
        }

        var now = attemptedAt ?? DateTime.UtcNow;
        Status = CallbackDeliveryStatus.PermanentFailure;
        LastAttemptAt = now;
        UpdatedAt = now;
        NextRetryAt = null;
        LastError = error.Trim();
        AttemptCount++;
    }
}
