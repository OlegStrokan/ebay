using Application.Commands.CapturePayment;
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

public class CapturePaymentCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IStripePaymentProvider _stripePaymentProvider = Substitute.For<IStripePaymentProvider>();
    private readonly IOrderCallbackQueueService _orderCallbackQueueService =
        Substitute.For<IOrderCallbackQueueService>();
    private readonly IMoneyEventQueueService _moneyEventQueueService = Substitute.For<IMoneyEventQueueService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<CapturePaymentCommandHandler> _logger =
        NullLogger<CapturePaymentCommandHandler>.Instance;

    public CapturePaymentCommandHandlerTests()
    {
        _clock.UtcNow.Returns(FixedNow);
    }

    private CapturePaymentCommandHandler BuildHandler() =>
        new(_paymentRepository, _stripePaymentProvider, _orderCallbackQueueService, _moneyEventQueueService, _unitOfWork, _clock, _logger);

    private static CapturePaymentCommand ValidCommand(decimal amount = 100m) =>
        new(
            OrderId: "order-cap-1",
            CustomerId: "customer-cap-1",
            ProviderPaymentIntentId: "pi_test_123",
            Amount: amount,
            Currency: "usd",
            IdempotencyKey: "idem-cap-1");

    [Fact]
    public async Task Handle_ShouldReturnExistingPayment_WhenDuplicateIdempotencyKey()
    {
        var existing = Payment.Create(
            PaymentId.From("pay-existing-cap"),
            "order-cap-1",
            "customer-cap-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-cap-1"),
            FixedNow.AddMinutes(-5));

        existing.MarkSucceeded(ProviderPaymentIntentId.From("pi_test_123"), FixedNow.AddMinutes(-4));

        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(existing.Id.Value, result.Value!.PaymentId);
        Assert.Equal(ProcessPaymentStatus.Succeeded, result.Value.Status);

        await _stripePaymentProvider.DidNotReceive().CapturePaymentAsync(
            Arg.Any<CapturePaymentProviderRequest>(),
            Arg.Any<CancellationToken>());

        await _paymentRepository.DidNotReceive().AddAsync(
            Arg.Any<Payment>(),
            Arg.Any<CancellationToken>());

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldPersistPaymentAndReturnSucceeded_WhenCaptureSucceeds()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: "pi_test_123",
                ErrorCode: null,
                ErrorMessage: null));

        Payment? addedPayment = null;
        _paymentRepository
            .When(x => x.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => addedPayment = callInfo.Arg<Payment>());

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(ProcessPaymentStatus.Succeeded, result.Value!.Status);
        Assert.Equal("pi_test_123", result.Value.ProviderPaymentIntentId);

        Assert.NotNull(addedPayment);
        Assert.Equal("order-cap-1", addedPayment!.OrderId);
        Assert.Equal(PaymentStatus.Succeeded, addedPayment.Status);
        Assert.Equal("pi_test_123", addedPayment.ProviderPaymentIntentId?.Value);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldPersistPaymentAndReturnFailed_WhenCaptureProviderReturnsFailed()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "provider_capture_failed",
                ErrorMessage: "Card declined during capture."));

        Payment? addedPayment = null;
        _paymentRepository
            .When(x => x.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => addedPayment = callInfo.Arg<Payment>());

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(ProcessPaymentStatus.Failed, result.Value!.Status);
        Assert.Equal("provider_capture_failed", result.Value.ErrorCode);

        Assert.NotNull(addedPayment);
        Assert.Equal(PaymentStatus.Failed, addedPayment!.Status);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenDomainValidationFails_InvalidAmount()
    {
        var result = await BuildHandler().Handle(ValidCommand(amount: 0m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("greater", result.Errors[0], StringComparison.OrdinalIgnoreCase);

        await _stripePaymentProvider.DidNotReceive().CapturePaymentAsync(
            Arg.Any<CapturePaymentProviderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldPassCorrectFieldsToProvider()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        CapturePaymentProviderRequest? capturedProviderRequest = null;
        _stripePaymentProvider
            .CapturePaymentAsync(
                Arg.Do<CapturePaymentProviderRequest>(r => capturedProviderRequest = r),
                Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: "pi_test_123",
                ErrorCode: null,
                ErrorMessage: null));

        await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.NotNull(capturedProviderRequest);
        Assert.Equal("pi_test_123", capturedProviderRequest!.ProviderPaymentIntentId);
        Assert.Equal("order-cap-1", capturedProviderRequest.OrderId);
        Assert.Equal("customer-cap-1", capturedProviderRequest.CustomerId);
        Assert.Equal(100m, capturedProviderRequest.Amount);
        Assert.Equal("USD", capturedProviderRequest.Currency);
        Assert.False(string.IsNullOrWhiteSpace(capturedProviderRequest.IdempotencyKey));
    }

    [Fact]
    public async Task Handle_ShouldQueuePaymentSucceededCallback_WhenCaptureSucceeds()
    {
        // Capture is where money actually moves, and it used to have no async completion path at
        // all: if this response is lost the caller's saga parks Uncertain and nothing ever tells
        // it the capture landed. The queued callback is that push path.
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: "pi_test_123",
                ErrorCode: null,
                ErrorMessage: null));

        await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        await _orderCallbackQueueService.Received(1).QueuePaymentSucceededAsync(
            Arg.Is<Payment>(p => p.Status == PaymentStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldQueueOneCapturedMoneyEvent_WhenCaptureSucceeds()
    {
        // The ledger posting must be written in the same transaction as the money mutation,
        // otherwise a crash after capture leaves the ledger permanently short.
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: "pi_test_123",
                ErrorCode: null,
                ErrorMessage: null));

        await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        await _moneyEventQueueService.Received(1).QueuePaymentCapturedAsync(
            Arg.Is<Payment>(p => p.Status == PaymentStatus.Succeeded),
            Arg.Any<CancellationToken>());

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldNotQueueMoneyEvent_WhenCaptureFails()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: "pi_test_123",
                ErrorCode: "card_declined",
                ErrorMessage: "Card declined."));

        await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        await _moneyEventQueueService.DidNotReceive().QueuePaymentCapturedAsync(
            Arg.Any<Payment>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldQueuePaymentFailedCallback_WhenCaptureFails()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "provider_capture_failed",
                ErrorMessage: "Card declined during capture."));

        await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        await _orderCallbackQueueService.Received(1).QueuePaymentFailedAsync(
            Arg.Is<Payment>(p => p.Status == PaymentStatus.Failed),
            Arg.Any<FailureReason>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldCaptureAuthorizedPreAuthAndQueueCallback_WhenHoldExists()
    {
        // The pre-auth path: AuthorizePayment left the payment Authorized, capture consumes the
        // hold. This is the exact shape of the order flow, so it must produce a callback too.
        var authorized = Payment.Create(
            PaymentId.From("pay-authorized-cap"),
            "order-cap-1",
            "customer-cap-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-auth-1"),
            FixedNow.AddMinutes(-5));

        authorized.MarkAuthorized(ProviderPaymentIntentId.From("pi_test_123"), FixedNow.AddMinutes(-4));

        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _paymentRepository
            .GetByProviderPaymentIntentIdAsync(
                Arg.Any<ProviderPaymentIntentId>(),
                Arg.Any<CancellationToken>())
            .Returns(authorized);

        _stripePaymentProvider
            .CapturePaymentAsync(Arg.Any<CapturePaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: "pi_test_123",
                ErrorCode: null,
                ErrorMessage: null));

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Succeeded, authorized.Status);

        await _paymentRepository.Received(1).UpdateAsync(authorized, Arg.Any<CancellationToken>());
        await _paymentRepository.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());

        await _orderCallbackQueueService.Received(1).QueuePaymentSucceededAsync(
            authorized,
            Arg.Any<CancellationToken>());
    }
}
