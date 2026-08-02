using Application.Commands.ReconcilePendingPayments;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Models;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests.Commands;

public class ReconcilePendingPaymentsCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 24, 18, 0, 0, DateTimeKind.Utc);

    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IRefundRepository _refundRepository = Substitute.For<IRefundRepository>();
    private readonly IStripePaymentProvider _stripePaymentProvider = Substitute.For<IStripePaymentProvider>();
    private readonly IOrderCallbackQueueService _queueService = Substitute.For<IOrderCallbackQueueService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<ReconcilePendingPaymentsCommandHandler> _logger = NullLogger<ReconcilePendingPaymentsCommandHandler>.Instance;

    public ReconcilePendingPaymentsCommandHandlerTests()
    {
        _clock.UtcNow.Returns(FixedNow);

        // Authorized holds are reconciled on the same pass now; default to none so the tests that
        // only care about pending payments stay focused.
        _paymentRepository.GetAuthorizedOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Payment>());
    }

    private ReconcilePendingPaymentsCommandHandler BuildHandler() =>
        new(_paymentRepository, _refundRepository, _stripePaymentProvider, _queueService, _unitOfWork, _clock, _logger);

    private static Payment CreatePendingPayment(
        string paymentId = "pay-1",
        string orderId = "order-1",
        string customerId = "customer-1",
        string idempotencyKey = "idem-1",
        string providerPaymentIntentId = "pi_1")
    {
        var payment = Payment.Create(
            PaymentId.From(paymentId),
            orderId,
            customerId,
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From(idempotencyKey),
            FixedNow.AddMinutes(-30));

        payment.MarkPendingProviderConfirmation(ProviderPaymentIntentId.From(providerPaymentIntentId), FixedNow.AddMinutes(-29));
        return payment;
    }

    private static Refund CreatePendingRefund(Payment payment)
    {
        var refund = Refund.Create(
            payment.Id,
            Money.Create(30m, "USD"),
            "customer request",
            IdempotencyKey.From("refund-idem"),
            FixedNow.AddMinutes(-20));

        refund.MarkPendingProviderConfirmation(ProviderRefundId.From("re_1"), FixedNow.AddMinutes(-19));
        return refund;
    }

    [Fact]
    public async Task Handle_ShouldReconcilePaymentAndRefundAndQueueCallbacks()
    {
        var payment = CreatePendingPayment();
        var refund = CreatePendingRefund(payment);

        _paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([payment]);

        _refundRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([refund]);

        _paymentRepository.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<PaymentId>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<PaymentId, Payment>
            {
                [payment.Id] = payment,
            });

        _stripePaymentProvider.GetPaymentStatusAsync("pi_1", Arg.Any<CancellationToken>())
            .Returns(new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Succeeded, null, null));

        _stripePaymentProvider.GetRefundStatusAsync("re_1", Arg.Any<CancellationToken>())
            .Returns(new ProviderRefundStatusResult(ProviderRefundLifecycleStatus.Failed, "RF_FAIL", "provider fail"));

        var result = await BuildHandler().Handle(new ReconcilePendingPaymentsCommand(15, 100), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value!.PaymentsChecked);
        Assert.Equal(1, result.Value.PaymentsSucceeded);
        Assert.Equal(1, result.Value.RefundsChecked);
        Assert.Equal(1, result.Value.RefundsFailed);
        Assert.Equal(2, result.Value.CallbacksQueued);

        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenUnexpectedExceptionOccurs()
    {
        _paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Payment>>>(_ => throw new InvalidOperationException("boom"));

        var result = await BuildHandler().Handle(new ReconcilePendingPaymentsCommand(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Unexpected error", result.Errors[0]);
    }

    [Fact]
    public async Task Handle_ShouldDetachUncommittedChanges_WhenPaymentSaveFails_AndContinueWithNextPayment()
    {
        var firstPayment = CreatePendingPayment(
            paymentId: "pay-1",
            orderId: "order-1",
            customerId: "customer-1",
            idempotencyKey: "idem-1",
            providerPaymentIntentId: "pi_1");

        var secondPayment = CreatePendingPayment(
            paymentId: "pay-2",
            orderId: "order-2",
            customerId: "customer-2",
            idempotencyKey: "idem-2",
            providerPaymentIntentId: "pi_2");

        _paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([firstPayment, secondPayment]);

        _refundRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Refund>());

        _paymentRepository.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<PaymentId>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<PaymentId, Payment>());

        _stripePaymentProvider.GetPaymentStatusAsync("pi_1", Arg.Any<CancellationToken>())
            .Returns(new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Succeeded, null, null));

        _stripePaymentProvider.GetPaymentStatusAsync("pi_2", Arg.Any<CancellationToken>())
            .Returns(new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Succeeded, null, null));

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("db write failed"),
                _ => 1);

        var result = await BuildHandler().Handle(new ReconcilePendingPaymentsCommand(15, 100), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.PaymentsChecked);
        Assert.Equal(2, result.Value.PaymentsSucceeded);

        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        _unitOfWork.Received(1).DetachUncommittedChanges();
        _unitOfWork.DidNotReceive().ClearTrackedChanges();
    }

    private static Payment CreateAuthorizedPayment(
        string paymentId = "pay-auth-1",
        string providerPaymentIntentId = "pi_auth_1")
    {
        var payment = Payment.Create(
            PaymentId.From(paymentId),
            "order-auth-1",
            "customer-auth-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-auth-1"),
            FixedNow.AddMinutes(-30));

        payment.MarkAuthorized(ProviderPaymentIntentId.From(providerPaymentIntentId), FixedNow.AddMinutes(-29));
        return payment;
    }

    [Fact]
    public async Task Handle_ShouldResolveAuthorizedPayment_WhenProviderReportsItCaptured()
    {
        // The lost-capture-response case from the pull side: the hold was captured at the provider
        // but our row still says Authorized. Excluding Authorized from reconciliation is what made
        // that state unrecoverable - the money moved and nothing downstream was ever told.
        var authorized = CreateAuthorizedPayment();

        _paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Payment>());

        _paymentRepository.GetAuthorizedOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([authorized]);

        _refundRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Refund>());

        _paymentRepository.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<PaymentId>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<PaymentId, Payment>());

        _stripePaymentProvider.GetPaymentStatusAsync("pi_auth_1", Arg.Any<CancellationToken>())
            .Returns(new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Succeeded, null, null));

        var result = await BuildHandler().Handle(new ReconcilePendingPaymentsCommand(15, 100), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.PaymentsChecked);
        Assert.Equal(1, result.Value.PaymentsSucceeded);
        Assert.Equal(PaymentStatus.Succeeded, authorized.Status);

        await _queueService.Received(1).QueuePaymentSucceededAsync(authorized, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldFailAuthorizedPayment_WhenProviderReportsTheHoldGone()
    {
        // A voided or expired hold. The Payment row claimed the funds were still held; now it
        // stops claiming that, and the order is told.
        var authorized = CreateAuthorizedPayment();

        _paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Payment>());

        _paymentRepository.GetAuthorizedOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([authorized]);

        _refundRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Refund>());

        _paymentRepository.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<PaymentId>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<PaymentId, Payment>());

        _stripePaymentProvider.GetPaymentStatusAsync("pi_auth_1", Arg.Any<CancellationToken>())
            .Returns(new ProviderPaymentStatusResult(
                ProviderPaymentLifecycleStatus.Failed,
                "authorization_expired",
                "Authorization hold expired before capture."));

        var result = await BuildHandler().Handle(new ReconcilePendingPaymentsCommand(15, 100), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.PaymentsFailed);
        Assert.Equal(PaymentStatus.Failed, authorized.Status);

        await _queueService.Received(1).QueuePaymentFailedAsync(
            authorized,
            Arg.Any<FailureReason>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldLeaveAuthorizedPaymentAlone_WhileTheHoldIsStillLive()
    {
        // requires_capture maps to "pending" at the provider: the hold is fine, capture just has
        // not happened yet. Reconciliation must not touch it.
        var authorized = CreateAuthorizedPayment();

        _paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Payment>());

        _paymentRepository.GetAuthorizedOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([authorized]);

        _refundRepository.GetPendingProviderConfirmationsOlderThanAsync(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Refund>());

        _paymentRepository.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<PaymentId>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<PaymentId, Payment>());

        _stripePaymentProvider.GetPaymentStatusAsync("pi_auth_1", Arg.Any<CancellationToken>())
            .Returns(new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Pending, null, null));

        var result = await BuildHandler().Handle(new ReconcilePendingPaymentsCommand(15, 100), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.PaymentsChecked);
        Assert.Equal(0, result.Value.PaymentsSucceeded);
        Assert.Equal(0, result.Value.PaymentsFailed);
        Assert.Equal(PaymentStatus.Authorized, authorized.Status);

        await _queueService.DidNotReceive().QueuePaymentSucceededAsync(
            Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
