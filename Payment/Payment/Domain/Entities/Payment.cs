using Domain.Common;
using Domain.Enums;
using Domain.Events;
using Domain.Exceptions;
using Domain.Services;
using Domain.ValueObjects;

namespace Domain.Entities;

public sealed class Payment : AggregateRoot<PaymentId>
{
    public string OrderId { get; private set; } = string.Empty;

    public string CustomerId { get; private set; } = string.Empty;

    public Money Amount { get; private set; } = new(0, "USD");
    
    public PaymentMethod Method { get; private set; }

    public IdempotencyKey ProcessIdempotencyKey { get; private set; } = IdempotencyKey.From("unset");
    
    public PaymentStatus Status { get; private set; }
    
    public ProviderPaymentIntentId? ProviderPaymentIntentId { get; private set; }
    
    public ProviderRefundId? ProviderRefundId { get; private set; }
    
    public decimal TotalRefundedAmount { get; private set; }
    
    public FailureReason? FailureReason { get; private set; }
    
    public DateTime CreatedAt { get; private set; }
    
    public DateTime UpdatedAt { get; private set; }
    
    public DateTime? SucceededAt { get; private set; }
    
    public DateTime? FailedAt { get; private set; }

    private Payment() {}
    
    private Payment(
        PaymentId id,
        string orderId,
        string customerId,
        Money amount,
        PaymentMethod method,
        IdempotencyKey processIdempotencyKey,
        DateTime createdAt)
        : base(id)
    {
        OrderId = orderId;
        CustomerId = customerId;
        Amount = amount;
        Method = method;
        ProcessIdempotencyKey = processIdempotencyKey;
        Status = PaymentStatus.Created;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public static Payment Create(
        PaymentId id,
        string orderId,
        string customerId,
        Money amount,
        PaymentMethod method,
        IdempotencyKey processIdempotencyKey,
        DateTime? createdAt = null)
    
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            throw new InvalidValueException("Order id cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new InvalidValueException("Customer id cannot be empty");
        }

        if (!amount.IsGreaterThanZero())
        {
            throw new InvalidValueException("Payment amount must be greater then zero");
        }

        var now = createdAt ?? DateTime.UtcNow;

        var payment = new Payment(
            id,
            orderId.Trim(),
            customerId.Trim(),
            amount,
            method,
            processIdempotencyKey,
            now);

        payment.AddDomainEvent(new PaymentCreatedEvent(
            payment.Id,
            payment.OrderId,
            payment.CustomerId,
            payment.Amount,
            payment.Method,
            now));

        return payment;
    }

    public void MarkPendingProviderConfirmation(ProviderPaymentIntentId providerPaymentIntentId,
        DateTime? pendingAt = null)
    {
        PaymentStateMachine.EnsureCanTransition(Status, PaymentStatus.PendingProviderConfirmation);

        var now = pendingAt ?? DateTime.UtcNow;
        ProviderPaymentIntentId = providerPaymentIntentId;
        Status = PaymentStatus.PendingProviderConfirmation;
        FailureReason = null;
        UpdatedAt = now;
        
        AddDomainEvent(new PaymentPendingProviderConfirmationEvent(Id, providerPaymentIntentId, now));
    }

    public void MarkSucceeded(ProviderPaymentIntentId? providerPaymentIntentId = null, DateTime? succeededAt = null)
    {
        PaymentStateMachine.EnsureCanTransition(Status, PaymentStatus.Succeeded);

        var now = succeededAt ?? DateTime.UtcNow;
        ProviderPaymentIntentId = providerPaymentIntentId ?? ProviderPaymentIntentId;
        Status = PaymentStatus.Succeeded;
        FailureReason = null;
        SucceededAt = now;
        UpdatedAt = now;

        AddDomainEvent(new PaymentSucceededEvent(Id, ProviderPaymentIntentId, now));
    }

    public void MarkFailed(FailureReason reason, DateTime? failedAt = null)
    {
        PaymentStateMachine.EnsureCanTransition(Status, PaymentStatus.Failed);

        var now = failedAt ?? DateTime.UtcNow;
        Status = PaymentStatus.Failed;
        FailureReason = reason;
        FailedAt = now;
        UpdatedAt = now;

        AddDomainEvent(new PaymentFailedEvent(Id, reason, now));
    }
    
     public void StartRefund(RefundId refundId, Money amount, string reason, DateTime? requestedAt = null)
    {
        if (!amount.IsGreaterThanZero())
        {
            throw new InvalidValueException("Refund amount must be greater than zero");
        }

        if (amount.Amount > Amount.Amount - TotalRefundedAmount)
        {
            throw new DomainException("Refund amount cannot exceed remaining refundable amount");
        }

        if (!string.Equals(amount.Currency, Amount.Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Refund currency '{amount.Currency}' must match payment currency '{Amount.Currency}'");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidValueException("Refund reason cannot be empty.");
        }

        PaymentStateMachine.EnsureCanTransition(Status, PaymentStatus.RefundPending);

        var now = requestedAt ?? DateTime.UtcNow;
        Status = PaymentStatus.RefundPending;
        FailureReason = null;
        UpdatedAt = now;

        AddDomainEvent(new RefundRequestedEvent(Id, refundId, amount, reason.Trim(), now));
    }

    public void MarkRefunded(RefundId refundId, ProviderRefundId? providerRefundId = null, Money? amountRefunded = null, DateTime? refundedAt = null)
    {
        var now = refundedAt ?? DateTime.UtcNow;
        ProviderRefundId = providerRefundId ?? ProviderRefundId;
        FailureReason = null;

        if (amountRefunded is not null)
        {
            TotalRefundedAmount += amountRefunded.Amount;
        }

        var isFullyRefunded = amountRefunded is null || TotalRefundedAmount >= Amount.Amount;
        var targetStatus = isFullyRefunded ? PaymentStatus.Refunded : PaymentStatus.Succeeded;

        PaymentStateMachine.EnsureCanTransition(Status, targetStatus);
        Status = targetStatus;
        UpdatedAt = now;

        AddDomainEvent(new RefundSucceededEvent(Id, refundId, ProviderRefundId, now));
    }

    public void MarkRefundFailed(RefundId refundId, FailureReason reason, DateTime? failedAt = null)
    {
        PaymentStateMachine.EnsureCanTransition(Status, PaymentStatus.RefundFailed);

        var now = failedAt ?? DateTime.UtcNow;
        Status = PaymentStatus.RefundFailed;
        FailureReason = reason;
        UpdatedAt = now;

        AddDomainEvent(new RefundFailedEvent(Id, refundId, reason, now));
    }

    public void QueueOrderCallback(string callbackEventId, string callbackType, DateTime? queuedAt = null)
    {
        if (string.IsNullOrWhiteSpace(callbackEventId))
        {
            throw new InvalidValueException("Callback event id cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(callbackType))
        {
            throw new InvalidValueException("Callback type cannot be empty");
        }

        var now = queuedAt ?? DateTime.UtcNow;
        AddDomainEvent(new OrderCallbackQueuedEvent(Id, callbackEventId.Trim(), callbackType.Trim(), OrderId, now));
    }
}