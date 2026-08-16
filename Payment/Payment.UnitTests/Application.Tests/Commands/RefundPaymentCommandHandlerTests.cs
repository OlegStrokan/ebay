using Application.Commands.RefundPayment;
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

public class RefundPaymentCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 24, 14, 0, 0, DateTimeKind.Utc);

    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IRefundRepository _refundRepository = Substitute.For<IRefundRepository>();
    private readonly IStripePaymentProvider _stripePaymentProvider = Substitute.For<IStripePaymentProvider>();
    private readonly IMoneyEventQueueService _moneyEventQueueService = Substitute.For<IMoneyEventQueueService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<RefundPaymentCommandHandler> _logger = NullLogger<RefundPaymentCommandHandler>.Instance;

    public RefundPaymentCommandHandlerTests()
    {
        _clock.UtcNow.Returns(FixedNow);
    }

    private RefundPaymentCommandHandler BuildHandler() =>
        new(_paymentRepository, _refundRepository, _stripePaymentProvider, _moneyEventQueueService, _unitOfWork, _clock, _logger);

    private static Payment CreateSucceededPayment()
    {
        var payment = Payment.Create(
            PaymentId.From("pay-1"),
            "order-1",
            "customer-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("pay-idem"),
            FixedNow.AddMinutes(-10));

        payment.MarkSucceeded(ProviderPaymentIntentId.From("pi_1"), FixedNow.AddMinutes(-9));
        return payment;
    }

    private static RefundPaymentCommand ValidCommand() =>
        new("pay-1", 50m, "USD", "requested_by_customer", "refund-idem");

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenPaymentNotFound()
    {
        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("was not found", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_ShouldReturnExistingRefund_WhenDuplicateIdempotencyKey()
    {
        var payment = CreateSucceededPayment();
        var refund = Refund.Create(
            payment.Id,
            Money.Create(50m, "USD"),
            "requested_by_customer",
            IdempotencyKey.From("refund-idem"),
            FixedNow.AddMinutes(-1));

        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns(payment);

        _refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(
                Arg.Any<PaymentId>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns(refund);

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(refund.Id.Value, result.Value!.RefundId);

        await _stripePaymentProvider.DidNotReceive().RefundPaymentAsync(
            Arg.Any<RefundPaymentProviderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenPendingMissingProviderRefundId()
    {
        var payment = CreateSucceededPayment();

        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns(payment);
        _refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(
                Arg.Any<PaymentId>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Refund?)null);

        _stripePaymentProvider.RefundPaymentAsync(Arg.Any<RefundPaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundPaymentProviderResult(
                ProviderRefundPaymentStatus.Pending,
                ProviderRefundId: null,
                ErrorCode: null,
                ErrorMessage: null));

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("ProviderRefundId is required", result.Errors[0]);

        await _refundRepository.DidNotReceive().AddAsync(Arg.Any<Refund>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldPersistAndReturnSucceeded_WhenProviderSucceeds()
    {
        var payment = CreateSucceededPayment();

        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns(payment);
        _refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(
                Arg.Any<PaymentId>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Refund?)null);

        _stripePaymentProvider.RefundPaymentAsync(Arg.Any<RefundPaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundPaymentProviderResult(
                ProviderRefundPaymentStatus.Succeeded,
                ProviderRefundId: "re_123",
                ErrorCode: null,
                ErrorMessage: null));

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(RefundPaymentStatus.Succeeded, result.Value!.Status);
        Assert.Equal("re_123", result.Value.ProviderRefundId);

        await _refundRepository.Received(1).AddAsync(Arg.Any<Refund>(), Arg.Any<CancellationToken>());
        await _paymentRepository.Received(1).UpdateAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        // The refund cash leg has exactly one owner: this money-event feeds the ledger.
        await _moneyEventQueueService.Received(1).QueueRefundIssuedAsync(
            Arg.Any<Payment>(),
            Arg.Is<Refund>(r => r.Status == RefundStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldNotQueueMoneyEvent_WhenProviderRefundIsPending()
    {
        var payment = CreateSucceededPayment();

        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns(payment);
        _refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(
                Arg.Any<PaymentId>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Refund?)null);

        _stripePaymentProvider.RefundPaymentAsync(Arg.Any<RefundPaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundPaymentProviderResult(
                ProviderRefundPaymentStatus.Pending,
                ProviderRefundId: "re_pending",
                ErrorCode: null,
                ErrorMessage: null));

        await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        // No money moved yet. Webhook or reconciliation emits the event when it settles.
        await _moneyEventQueueService.DidNotReceive().QueueRefundIssuedAsync(
            Arg.Any<Payment>(),
            Arg.Any<Refund>(),
            Arg.Any<CancellationToken>());
    }
}
