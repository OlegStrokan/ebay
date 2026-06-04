using Domain.Entities;
using Domain.Enums;
using Domain.Events;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Tests.Entities;

public class PaymentTests
{
    private readonly PaymentId _paymentId = PaymentId.CreateUnique();
    private readonly Money _amount = Money.Create(100m, "USD");
    private readonly IdempotencyKey _idempotencyKey = IdempotencyKey.From("order-1-attempt-1");

    private Payment CreatePayment(Money? amount = null) =>
        Payment.Create(
            _paymentId,
            "order-123",
            "customer-456",
            amount ?? _amount,
            PaymentMethod.Card,
            _idempotencyKey,
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    private Payment CreateSucceededPayment()
    {
        var p = CreatePayment();
        p.ClearDomainEvents();
        p.MarkSucceeded(ProviderPaymentIntentId.From("pi_stripe_abc"), new DateTime(2026, 1, 1, 12, 1, 0, DateTimeKind.Utc));
        p.ClearDomainEvents();
        return p;
    }

    #region Create

    [Fact]
    public void Create_ShouldSetStatusToCreated()
    {
        var payment = CreatePayment();

        Assert.Equal(PaymentStatus.Created, payment.Status);
    }

    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var createdAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var payment = Payment.Create(_paymentId, "order-123", "customer-456", _amount, PaymentMethod.Card, _idempotencyKey, createdAt);

        Assert.Equal("order-123", payment.OrderId);
        Assert.Equal("customer-456", payment.CustomerId);
        Assert.Equal(_amount, payment.Amount);
        Assert.Equal(PaymentMethod.Card, payment.Method);
        Assert.Equal(_idempotencyKey, payment.ProcessIdempotencyKey);
        Assert.Equal(createdAt, payment.CreatedAt);
        Assert.Equal(createdAt, payment.UpdatedAt);
        Assert.Null(payment.ProviderPaymentIntentId);
        Assert.Null(payment.FailureReason);
    }

    [Fact]
    public void Create_ShouldRaisePaymentCreatedEvent()
    {
        var payment = CreatePayment();

        Assert.Single(payment.DomainEvents);
        var evt = Assert.IsType<PaymentCreatedEvent>(payment.DomainEvents[0]);
        Assert.Equal("order-123", evt.OrderId);
        Assert.Equal("customer-456", evt.CustomerId);
        Assert.Equal(_amount, evt.Amount);
        Assert.Equal(PaymentMethod.Card, evt.PaymentMethod);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyOrderId_ShouldThrowInvalidValueException(string orderId)
    {
        var ex = Assert.Throws<InvalidValueException>(() =>
            Payment.Create(_paymentId, orderId, "cust-1", _amount, PaymentMethod.Card, _idempotencyKey));

        Assert.Contains("Order id cannot be empty", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyCustomerId_ShouldThrowInvalidValueException(string customerId)
    {
        var ex = Assert.Throws<InvalidValueException>(() =>
            Payment.Create(_paymentId, "order-1", customerId, _amount, PaymentMethod.Card, _idempotencyKey));

        Assert.Contains("Customer id cannot be empty", ex.Message);
    }

    [Fact]
    public void Create_WithZeroAmount_ShouldThrowInvalidValueException()
    {
        var zeroAmount = Money.Create(0, "USD");

        var ex = Assert.Throws<InvalidValueException>(() =>
            Payment.Create(_paymentId, "order-1", "cust-1", zeroAmount, PaymentMethod.Card, _idempotencyKey));

        Assert.Contains("Payment amount must be greater", ex.Message);
    }

    #endregion

    #region MarkPendingProviderConfirmation

    [Fact]
    public void MarkPendingProviderConfirmation_FromCreated_ShouldChangeStatus()
    {
        var payment = CreatePayment();
        payment.ClearDomainEvents();

        payment.MarkPendingProviderConfirmation(ProviderPaymentIntentId.From("pi_abc"));

        Assert.Equal(PaymentStatus.PendingProviderConfirmation, payment.Status);
    }

    [Fact]
    public void MarkPendingProviderConfirmation_ShouldSetProviderPaymentIntentId()
    {
        var payment = CreatePayment();
        var providerIntentId = ProviderPaymentIntentId.From("pi_stripe_xyz");

        payment.MarkPendingProviderConfirmation(providerIntentId);

        Assert.Equal(providerIntentId, payment.ProviderPaymentIntentId);
    }

    [Fact]
    public void MarkPendingProviderConfirmation_ShouldRaiseEvent()
    {
        var payment = CreatePayment();
        payment.ClearDomainEvents();
        var providerIntentId = ProviderPaymentIntentId.From("pi_stripe_xyz");

        payment.MarkPendingProviderConfirmation(providerIntentId);

        var evt = Assert.IsType<PaymentPendingProviderConfirmationEvent>(payment.DomainEvents[0]);
        Assert.Equal(providerIntentId, evt.ProviderPaymentIntentId);
    }

    [Fact]
    public void MarkPendingProviderConfirmation_FromFailed_ShouldThrow()
    {
        var payment = CreatePayment();
        payment.MarkFailed(FailureReason.Create("err", "failed"));

        Assert.Throws<InvalidPaymentStateTransitionException>(() =>
            payment.MarkPendingProviderConfirmation(ProviderPaymentIntentId.From("pi_abc")));
    }

    #endregion

    #region MarkSucceeded

    [Fact]
    public void MarkSucceeded_FromCreated_ShouldChangeStatusToSucceeded()
    {
        var payment = CreatePayment();
        payment.ClearDomainEvents();

        payment.MarkSucceeded();

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
    }

    [Fact]
    public void MarkSucceeded_FromPendingProviderConfirmation_ShouldChangeStatus()
    {
        var payment = CreatePayment();
        payment.MarkPendingProviderConfirmation(ProviderPaymentIntentId.From("pi_abc"));
        payment.ClearDomainEvents();

        payment.MarkSucceeded();

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
    }

    [Fact]
    public void MarkSucceeded_ShouldSetSucceededAt()
    {
        var payment = CreatePayment();
        var succeededAt = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);

        payment.MarkSucceeded(succeededAt: succeededAt);

        Assert.Equal(succeededAt, payment.SucceededAt);
    }

    [Fact]
    public void MarkSucceeded_ShouldClearFailureReason()
    {
        var payment = CreatePayment();
        payment.MarkPendingProviderConfirmation(ProviderPaymentIntentId.From("pi_abc"));
        payment.ClearDomainEvents();
        payment.MarkSucceeded();

        Assert.Null(payment.FailureReason);
    }

    [Fact]
    public void MarkSucceeded_ShouldRaisePaymentSucceededEvent()
    {
        var payment = CreatePayment();
        payment.ClearDomainEvents();

        payment.MarkSucceeded(ProviderPaymentIntentId.From("pi_abc"));

        var evt = Assert.IsType<PaymentSucceededEvent>(payment.DomainEvents[0]);
        Assert.NotNull(evt.ProviderPaymentIntentId);
    }

    [Fact]
    public void MarkSucceeded_FromFailed_ShouldThrow()
    {
        var payment = CreatePayment();
        payment.MarkFailed(FailureReason.Create("err", "failed"));

        Assert.Throws<InvalidPaymentStateTransitionException>(() => payment.MarkSucceeded());
    }

    #endregion

    #region MarkFailed

    [Fact]
    public void MarkFailed_FromCreated_ShouldChangeStatusToFailed()
    {
        var payment = CreatePayment();
        var reason = FailureReason.Create("card_declined", "Card was declined.");

        payment.MarkFailed(reason);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
    }

    [Fact]
    public void MarkFailed_ShouldSetFailureReason()
    {
        var payment = CreatePayment();
        var reason = FailureReason.Create("card_declined", "Card was declined.");

        payment.MarkFailed(reason);

        Assert.Equal(reason, payment.FailureReason);
    }

    [Fact]
    public void MarkFailed_ShouldSetFailedAt()
    {
        var payment = CreatePayment();
        var failedAt = new DateTime(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

        payment.MarkFailed(FailureReason.Create("err", "fail"), failedAt);

        Assert.Equal(failedAt, payment.FailedAt);
    }

    [Fact]
    public void MarkFailed_ShouldRaisePaymentFailedEvent()
    {
        var payment = CreatePayment();
        payment.ClearDomainEvents();
        var reason = FailureReason.Create("err", "fail");

        payment.MarkFailed(reason);

        var evt = Assert.IsType<PaymentFailedEvent>(payment.DomainEvents[0]);
        Assert.Equal(reason, evt.FailureReason);
    }

    [Fact]
    public void MarkFailed_FromSucceeded_ShouldThrow()
    {
        var payment = CreateSucceededPayment();

        Assert.Throws<InvalidPaymentStateTransitionException>(() =>
            payment.MarkFailed(FailureReason.Create("err", "fail")));
    }

    #endregion

    #region StartRefund

    [Fact]
    public void StartRefund_FromSucceeded_ShouldChangeStatusToRefundPending()
    {
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        var refundAmount = Money.Create(50, "USD");

        payment.StartRefund(refundId, refundAmount, "Customer request");

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
    }

    [Fact]
    public void StartRefund_ShouldRaiseRefundRequestedEvent()
    {
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        var refundAmount = Money.Create(50, "USD");

        payment.StartRefund(refundId, refundAmount, "Customer request");

        var evt = Assert.IsType<RefundRequestedEvent>(payment.DomainEvents[0]);
        Assert.Equal(refundId, evt.RefundId);
        Assert.Equal(refundAmount, evt.Amount);
        Assert.Equal("Customer request", evt.Reason);
    }

    [Fact]
    public void StartRefund_WithZeroAmount_ShouldThrowInvalidValueException()
    {
        var payment = CreateSucceededPayment();

        var ex = Assert.Throws<InvalidValueException>(() =>
            payment.StartRefund(RefundId.CreateUnique(), Money.Create(0, "USD"), "reason"));

        Assert.Contains("Refund amount must be greater than zero", ex.Message);
    }

    [Fact]
    public void StartRefund_WithAmountExceedingOriginal_ShouldThrowDomainException()
    {
        var payment = CreateSucceededPayment();

        Assert.Throws<DomainException>(() =>
            payment.StartRefund(RefundId.CreateUnique(), Money.Create(200, "USD"), "reason"));
    }

    [Fact]
    public void StartRefund_WithDifferentCurrency_ShouldThrowDomainException()
    {
        var payment = CreateSucceededPayment();

        Assert.Throws<DomainException>(() =>
            payment.StartRefund(RefundId.CreateUnique(), Money.Create(50, "EUR"), "reason"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StartRefund_WithEmptyReason_ShouldThrowInvalidValueException(string reason)
    {
        var payment = CreateSucceededPayment();

        Assert.Throws<InvalidValueException>(() =>
            payment.StartRefund(RefundId.CreateUnique(), Money.Create(50, "USD"), reason));
    }

    [Fact]
    public void StartRefund_FromCreated_ShouldThrow()
    {
        var payment = CreatePayment();

        Assert.Throws<InvalidPaymentStateTransitionException>(() =>
            payment.StartRefund(RefundId.CreateUnique(), Money.Create(50, "USD"), "reason"));
    }

    #endregion

    #region MarkRefunded

    [Fact]
    public void MarkRefunded_FullRefund_ShouldChangeStatusToRefunded()
    {
        var payment = CreateSucceededPayment();     // Amount = $100
        var refundId = RefundId.CreateUnique();
        payment.StartRefund(refundId, Money.Create(100, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefunded(refundId, amountRefunded: Money.Create(100, "USD"));

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(100m, payment.TotalRefundedAmount);
    }

    [Fact]
    public void MarkRefunded_PartialRefund_ShouldReturnToSucceeded()
    {
        var payment = CreateSucceededPayment();     // Amount = $100
        var refundId = RefundId.CreateUnique();
        payment.StartRefund(refundId, Money.Create(40, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefunded(refundId, amountRefunded: Money.Create(40, "USD"));

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(40m, payment.TotalRefundedAmount);
    }

    [Fact]
    public void MarkRefunded_TwoPartialRefunds_ShouldAllowSecondRefund()
    {
        var payment = CreateSucceededPayment();     // Amount = $100

        var refundId1 = RefundId.CreateUnique();
        payment.StartRefund(refundId1, Money.Create(30, "USD"), "first");
        payment.MarkRefunded(refundId1, amountRefunded: Money.Create(30, "USD"));

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(30m, payment.TotalRefundedAmount);

        var refundId2 = RefundId.CreateUnique();
        payment.StartRefund(refundId2, Money.Create(30, "USD"), "second");
        payment.MarkRefunded(refundId2, amountRefunded: Money.Create(30, "USD"));

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(60m, payment.TotalRefundedAmount);
    }

    [Fact]
    public void MarkRefunded_FinalPartialRefundExhaustsAmount_ShouldBeFullyRefunded()
    {
        var payment = CreateSucceededPayment();     // Amount = $100

        var refundId1 = RefundId.CreateUnique();
        payment.StartRefund(refundId1, Money.Create(60, "USD"), "first");
        payment.MarkRefunded(refundId1, amountRefunded: Money.Create(60, "USD"));

        var refundId2 = RefundId.CreateUnique();
        payment.StartRefund(refundId2, Money.Create(40, "USD"), "second");
        payment.MarkRefunded(refundId2, amountRefunded: Money.Create(40, "USD"));

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(100m, payment.TotalRefundedAmount);
    }

    [Fact]
    public void MarkRefunded_WithNoAmount_ShouldDefaultToRefunded()
    {
        // When no amount is supplied (backward compat / unknown amount), always go terminal.
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        payment.StartRefund(refundId, Money.Create(50, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefunded(refundId);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    [Fact]
    public void MarkRefunded_ShouldRaiseRefundSucceededEvent()
    {
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        payment.StartRefund(refundId, Money.Create(50, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefunded(refundId, ProviderRefundId.From("re_abc"));

        var evt = Assert.IsType<RefundSucceededEvent>(payment.DomainEvents[0]);
        Assert.Equal(refundId, evt.RefundId);
    }

    #endregion

    #region MarkRefundFailed

    [Fact]
    public void MarkRefundFailed_FromRefundPending_ShouldChangeStatusToRefundFailed()
    {
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        payment.StartRefund(refundId, Money.Create(50, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefundFailed(refundId, FailureReason.Create("err", "refund failed"));

        Assert.Equal(PaymentStatus.RefundFailed, payment.Status);
    }

    [Fact]
    public void MarkRefundFailed_ShouldSetFailureReason()
    {
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        var reason = FailureReason.Create("err", "refund failed");
        payment.StartRefund(refundId, Money.Create(50, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefundFailed(refundId, reason);

        Assert.Equal(reason, payment.FailureReason);
    }

    [Fact]
    public void MarkRefundFailed_ShouldRaiseRefundFailedEvent()
    {
        var payment = CreateSucceededPayment();
        var refundId = RefundId.CreateUnique();
        var reason = FailureReason.Create("err", "refund failed");
        payment.StartRefund(refundId, Money.Create(50, "USD"), "reason");
        payment.ClearDomainEvents();

        payment.MarkRefundFailed(refundId, reason);

        var evt = Assert.IsType<RefundFailedEvent>(payment.DomainEvents[0]);
        Assert.Equal(refundId, evt.RefundId);
        Assert.Equal(reason, evt.FailureReason);
    }

    #endregion

    #region QueueOrderCallback

    [Fact]
    public void QueueOrderCallback_ShouldRaiseOrderCallbackQueuedEvent()
    {
        var payment = CreatePayment();
        payment.ClearDomainEvents();

        payment.QueueOrderCallback("evt-001", "payment.succeeded");

        var evt = Assert.IsType<OrderCallbackQueuedEvent>(payment.DomainEvents[0]);
        Assert.Equal("evt-001", evt.CallbackEventId);
        Assert.Equal("payment.succeeded", evt.CallbackType);
        Assert.Equal("order-123", evt.OrderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void QueueOrderCallback_WithEmptyCallbackEventId_ShouldThrowInvalidValueException(string callbackEventId)
    {
        var payment = CreatePayment();

        Assert.Throws<InvalidValueException>(() =>
            payment.QueueOrderCallback(callbackEventId, "payment.succeeded"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void QueueOrderCallback_WithEmptyCallbackType_ShouldThrowInvalidValueException(string callbackType)
    {
        var payment = CreatePayment();

        Assert.Throws<InvalidValueException>(() =>
            payment.QueueOrderCallback("evt-001", callbackType));
    }

    #endregion
}

public class RefundTests
{
    private readonly PaymentId _paymentId = PaymentId.CreateUnique();
    private readonly Money _amount = Money.Create(50m, "USD");
    private readonly IdempotencyKey _idempotencyKey = IdempotencyKey.From("refund-order-1");

    private Refund CreateRefund(Money? amount = null) =>
        Refund.Create(_paymentId, amount ?? _amount, "Customer request", _idempotencyKey);

    #region Create

    [Fact]
    public void Create_ShouldSetStatusToRequested()
    {
        var refund = CreateRefund();

        Assert.Equal(RefundStatus.Requested, refund.Status);
    }

    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var createdAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var refund = Refund.Create(_paymentId, _amount, "Customer request", _idempotencyKey, createdAt);

        Assert.Equal(_paymentId, refund.PaymentId);
        Assert.Equal(_amount, refund.Amount);
        Assert.Equal("Customer request", refund.Reason);
        Assert.Equal(_idempotencyKey, refund.IdempotencyKey);
        Assert.Equal(createdAt, refund.CreatedAt);
        Assert.Equal(createdAt, refund.UpdatedAt);
        Assert.Null(refund.ProviderRefundId);
        Assert.Null(refund.FailureReason);
        Assert.Null(refund.CompletedAt);
    }

    [Fact]
    public void Create_WithZeroAmount_ShouldThrowInvalidValueException()
    {
        var ex = Assert.Throws<InvalidValueException>(() =>
            Refund.Create(_paymentId, Money.Create(0, "USD"), "reason", _idempotencyKey));

        Assert.Contains("Refund amount must be greater than zero", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyReason_ShouldThrowInvalidValueException(string reason)
    {
        var ex = Assert.Throws<InvalidValueException>(() =>
            Refund.Create(_paymentId, _amount, reason, _idempotencyKey));

        Assert.Contains("Refund reason cannot be empty", ex.Message);
    }

    [Fact]
    public void Create_ShouldTrimReason()
    {
        var refund = Refund.Create(_paymentId, _amount, "  trimmed reason  ", _idempotencyKey);

        Assert.Equal("trimmed reason", refund.Reason);
    }

    [Fact]
    public void Create_IdShouldBeUnique()
    {
        var r1 = CreateRefund();
        var r2 = CreateRefund();

        Assert.NotEqual(r1.Id, r2.Id);
    }

    #endregion

    #region MarkPendingProviderConfirmation

    [Fact]
    public void MarkPendingProviderConfirmation_FromRequested_ShouldChangeStatus()
    {
        var refund = CreateRefund();
        var providerRefundId = ProviderRefundId.From("re_stripe_abc");

        refund.MarkPendingProviderConfirmation(providerRefundId);

        Assert.Equal(RefundStatus.PendingProviderConfirmation, refund.Status);
    }

    [Fact]
    public void MarkPendingProviderConfirmation_ShouldSetProviderRefundId()
    {
        var refund = CreateRefund();
        var providerRefundId = ProviderRefundId.From("re_stripe_abc");

        refund.MarkPendingProviderConfirmation(providerRefundId);

        Assert.Equal(providerRefundId, refund.ProviderRefundId);
    }

    [Fact]
    public void MarkPendingProviderConfirmation_ShouldClearFailureReason()
    {
        var refund = CreateRefund();
        refund.MarkFailed(FailureReason.Create("err", "some error"));
        refund.MarkPendingProviderConfirmation(ProviderRefundId.From("re_abc"));

        Assert.Null(refund.FailureReason);
    }

    [Fact]
    public void MarkPendingProviderConfirmation_FromSucceeded_ShouldThrow()
    {
        var refund = CreateRefund();
        refund.MarkSucceeded(ProviderRefundId.From("re_abc"));

        Assert.Throws<InvalidRefundStateTransitionException>(() =>
            refund.MarkPendingProviderConfirmation(ProviderRefundId.From("re_xyz")));
    }

    #endregion

    #region MarkSucceeded

    [Fact]
    public void MarkSucceeded_FromRequested_ShouldChangeStatusToSucceeded()
    {
        var refund = CreateRefund();

        refund.MarkSucceeded(ProviderRefundId.From("re_abc"));

        Assert.Equal(RefundStatus.Succeeded, refund.Status);
    }

    [Fact]
    public void MarkSucceeded_ShouldSetProviderRefundIdAndCompletedAt()
    {
        var refund = CreateRefund();
        var providerRefundId = ProviderRefundId.From("re_stripe_abc");
        var succeededAt = new DateTime(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

        refund.MarkSucceeded(providerRefundId, succeededAt);

        Assert.Equal(providerRefundId, refund.ProviderRefundId);
        Assert.Equal(succeededAt, refund.CompletedAt);
    }

    [Fact]
    public void MarkSucceeded_ShouldClearFailureReason()
    {
        var refund = CreateRefund();
        refund.MarkFailed(FailureReason.Create("err", "error"));
        refund.MarkPendingProviderConfirmation(ProviderRefundId.From("re_abc"));
        refund.MarkSucceeded(ProviderRefundId.From("re_abc"));

        Assert.Null(refund.FailureReason);
    }

    [Fact]
    public void MarkSucceeded_FromFailed_ShouldThrow()
    {
        var refund = CreateRefund();
        refund.MarkFailed(FailureReason.Create("err", "error"));

        Assert.Throws<InvalidRefundStateTransitionException>(() =>
            refund.MarkSucceeded(ProviderRefundId.From("re_abc")));
    }

    #endregion

    #region MarkFailed

    [Fact]
    public void MarkFailed_FromRequested_ShouldChangeStatusToFailed()
    {
        var refund = CreateRefund();
        var reason = FailureReason.Create("err", "refund failed");

        refund.MarkFailed(reason);

        Assert.Equal(RefundStatus.Failed, refund.Status);
    }

    [Fact]
    public void MarkFailed_ShouldSetFailureReason()
    {
        var refund = CreateRefund();
        var reason = FailureReason.Create("err", "refund failed");

        refund.MarkFailed(reason);

        Assert.Equal(reason, refund.FailureReason);
    }

    [Fact]
    public void MarkFailed_FromSucceeded_ShouldThrow()
    {
        var refund = CreateRefund();
        refund.MarkSucceeded(ProviderRefundId.From("re_abc"));

        Assert.Throws<InvalidRefundStateTransitionException>(() =>
            refund.MarkFailed(FailureReason.Create("err", "fail")));
    }

    #endregion
}
