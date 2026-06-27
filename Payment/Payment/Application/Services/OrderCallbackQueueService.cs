using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Domain.Entities;
using Domain.Interfaces;
using Domain.ValueObjects;

namespace Application.Services;

internal sealed class OrderCallbackQueueService(
    IOutboundOrderCallbackRepository outboundOrderCallbackRepository,
    IOrderCallbackPayloadSerializer payloadSerializer,
    IClock clock) : IOrderCallbackQueueService
{
    public Task<OrderCallbackQueuedDto> QueuePaymentSucceededAsync(
        Payment payment,
        CancellationToken cancellationToken = default)
    {
        var callbackEventId = $"{payment.Id.Value}:payment_succeeded";
        var payload = payloadSerializer.SerializePaymentSucceeded(callbackEventId, payment);

        return QueueInternalAsync(
            payment,
            callbackEventId,
            OrderCallbackEventTypes.PaymentSucceeded,
            payload,
            cancellationToken);
    }

    public Task<OrderCallbackQueuedDto> QueuePaymentFailedAsync(
        Payment payment,
        FailureReason reason,
        CancellationToken cancellationToken = default)
    {
        var callbackEventId = $"{payment.Id.Value}:payment_failed";
        var payload = payloadSerializer.SerializePaymentFailed(callbackEventId, payment, reason);

        return QueueInternalAsync(
            payment,
            callbackEventId,
            OrderCallbackEventTypes.PaymentFailed,
            payload,
            cancellationToken);
    }

    public Task<OrderCallbackQueuedDto> QueueRefundSucceededAsync(
        Payment payment,
        Refund refund,
        CancellationToken cancellationToken = default)
    {

        var callbackEventId = $"{refund.Id.Value}:refund_succeeded";
        var payload = payloadSerializer.SerializeRefundSucceeded(callbackEventId, payment, refund);

        return QueueInternalAsync(
            payment,
            callbackEventId,
            OrderCallbackEventTypes.RefundSucceeded,
            payload,
            cancellationToken);
    }

    public Task<OrderCallbackQueuedDto> QueueRefundFailedAsync(
        Payment payment,
        Refund refund,
        FailureReason reason,
        CancellationToken cancellationToken = default)
    {
        var callbackEventId = $"{refund.Id.Value}:refund_failed";
        var payload = payloadSerializer.SerializeRefundFailed(callbackEventId, payment, refund, reason);

        return QueueInternalAsync(
            payment,
            callbackEventId,
            OrderCallbackEventTypes.RefundFailed,
            payload,
            cancellationToken);
    }

    private async Task<OrderCallbackQueuedDto> QueueInternalAsync(
        Payment payment,
        string callbackEventId,
        string callbackType,
        string payload,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var callback = OutboundOrderCallback.Create(
            callbackEventId,
            payment.OrderId,
            callbackType,
            payload,
            now);

        await outboundOrderCallbackRepository.AddAsync(callback, cancellationToken);

        payment.QueueOrderCallback(callbackEventId, callbackType, now);

        return new OrderCallbackQueuedDto(
            CallbackEventId: callbackEventId,
            PaymentId: payment.Id.Value,
            CallbackType: callbackType,
            OrderId: payment.OrderId,
            QueuedAt: now);
    }
}