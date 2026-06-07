using Application.Commands.CancelAuthorization;
using Application.Gateways;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests.Commands;

public class CancelAuthorizationCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IStripePaymentProvider _stripePaymentProvider = Substitute.For<IStripePaymentProvider>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<CancelAuthorizationCommandHandler> _logger =
        NullLogger<CancelAuthorizationCommandHandler>.Instance;

    public CancelAuthorizationCommandHandlerTests()
    {
        _clock.UtcNow.Returns(FixedNow);
    }

    private CancelAuthorizationCommandHandler BuildHandler() =>
        new(_paymentRepository, _stripePaymentProvider, _unitOfWork, _clock, _logger);

    private static Payment CreatePendingPayment(string providerPaymentIntentId = "pi_auth_1")
    {
        var payment = Payment.Create(
            PaymentId.From("pay-auth-1"),
            "order-auth-1",
            "customer-auth-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-auth-1"),
            FixedNow.AddMinutes(-20));

        payment.MarkPendingProviderConfirmation(
            ProviderPaymentIntentId.From(providerPaymentIntentId),
            FixedNow.AddMinutes(-19));

        return payment;
    }

    [Fact]
    public async Task Handle_ShouldMarkPaymentFailedAndPersist_WhenPaymentIsPending()
    {
        var payment = CreatePendingPayment();

        _paymentRepository.GetByProviderPaymentIntentIdAsync(
                Arg.Any<ProviderPaymentIntentId>(),
                Arg.Any<CancellationToken>())
            .Returns(payment);

        var result = await BuildHandler().Handle(
            new CancelAuthorizationCommand("pi_auth_1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("authorization_canceled", payment.FailureReason?.Code);

        await _stripePaymentProvider.Received(1).CancelAuthorizationAsync(
            "pi_auth_1",
            Arg.Any<CancellationToken>());

        await _paymentRepository.Received(1).UpdateAsync(payment, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldStillCancelProvider_WhenPaymentRecordNotFound()
    {
        _paymentRepository.GetByProviderPaymentIntentIdAsync(
                Arg.Any<ProviderPaymentIntentId>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        var result = await BuildHandler().Handle(
            new CancelAuthorizationCommand("pi_auth_missing"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        await _stripePaymentProvider.Received(1).CancelAuthorizationAsync(
            "pi_auth_missing",
            Arg.Any<CancellationToken>());

        await _paymentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Payment>(),
            Arg.Any<CancellationToken>());

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldBeIdempotent_WhenPaymentAlreadyMarkedAsAuthorizationCanceled()
    {
        var payment = CreatePendingPayment("pi_auth_done");
        payment.MarkFailed(
            FailureReason.Create("authorization_canceled", "Authorization canceled via CancelAuthorization request."),
            FixedNow.AddMinutes(-18));

        _paymentRepository.GetByProviderPaymentIntentIdAsync(
                Arg.Any<ProviderPaymentIntentId>(),
                Arg.Any<CancellationToken>())
            .Returns(payment);

        var result = await BuildHandler().Handle(
            new CancelAuthorizationCommand("pi_auth_done"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        await _stripePaymentProvider.DidNotReceive().CancelAuthorizationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _paymentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Payment>(),
            Arg.Any<CancellationToken>());

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}