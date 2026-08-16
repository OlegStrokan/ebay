using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Domain.Entities;
using Domain.Interfaces;

namespace Application.Services;

internal sealed class MoneyEventQueueService(
    IOutboundMoneyEventRepository outboundMoneyEventRepository,
    IMoneyEventPayloadSerializer payloadSerializer,
    IClock clock) : IMoneyEventQueueService
{
    public Task<MoneyEventQueuedDto> QueuePaymentAuthorizedAsync(
        Payment payment,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var eventId = $"{payment.Id.Value}:authorized";

        return QueueInternalAsync(
            payment,
            eventId,
            MoneyEventTypes.PaymentAuthorized,
            payloadSerializer.SerializePaymentAuthorized(eventId, payment, now),
            now,
            cancellationToken);
    }

    public Task<MoneyEventQueuedDto> QueuePaymentVoidedAsync(
        Payment payment,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var eventId = $"{payment.Id.Value}:voided";

        return QueueInternalAsync(
            payment,
            eventId,
            MoneyEventTypes.PaymentVoided,
            payloadSerializer.SerializePaymentVoided(eventId, payment, now),
            now,
            cancellationToken);
    }

    public Task<MoneyEventQueuedDto> QueuePaymentCapturedAsync(
        Payment payment,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var eventId = $"{payment.Id.Value}:captured";

        return QueueInternalAsync(
            payment,
            eventId,
            MoneyEventTypes.PaymentCaptured,
            payloadSerializer.SerializePaymentCaptured(eventId, payment, now),
            now,
            cancellationToken);
    }

    public Task<MoneyEventQueuedDto> QueueRefundIssuedAsync(
        Payment payment,
        Refund refund,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var eventId = $"{refund.Id.Value}:refunded";

        return QueueInternalAsync(
            payment,
            eventId,
            MoneyEventTypes.RefundIssued,
            payloadSerializer.SerializeRefundIssued(eventId, payment, refund, now),
            now,
            cancellationToken);
    }

    private async Task<MoneyEventQueuedDto> QueueInternalAsync(
        Payment payment,
        string eventId,
        string eventType,
        string payload,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Capture, webhook and reconciliation can all resolve the same payment, so the
        // deterministic event id collapses the repeats into one outbox row.
        var existing = await outboundMoneyEventRepository.GetByEventIdAsync(eventId, cancellationToken);

        if (existing is not null)
        {
            return new MoneyEventQueuedDto(
                EventId: existing.EventId,
                EventType: existing.EventType,
                PaymentId: existing.PaymentId,
                OrderId: existing.OrderId,
                QueuedAt: existing.CreatedAt);
        }

        var moneyEvent = OutboundMoneyEvent.Create(
            eventId,
            payment.Id.Value,
            payment.OrderId,
            eventType,
            payload,
            now);

        await outboundMoneyEventRepository.AddAsync(moneyEvent, cancellationToken);

        return new MoneyEventQueuedDto(
            EventId: eventId,
            EventType: eventType,
            PaymentId: payment.Id.Value,
            OrderId: payment.OrderId,
            QueuedAt: now);
    }
}
