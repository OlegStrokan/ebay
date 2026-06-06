using Application.Commands.ProcessPayment;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Models;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests.Commands;

public class ProcessPaymentCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IStripePaymentProvider _stripePaymentProvider = Substitute.For<IStripePaymentProvider>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<ProcessPaymentCommandHandler> _logger =
        NullLogger<ProcessPaymentCommandHandler>.Instance;

    public ProcessPaymentCommandHandlerTests()
    {
        _clock.UtcNow.Returns(FixedNow);
    }

    private ProcessPaymentCommandHandler BuildHandler() =>
        new(_paymentRepository, _stripePaymentProvider, _unitOfWork, _clock, _logger);

    private static ProcessPaymentCommand ValidCommand(decimal amount = 100m) =>
        new(
            OrderId: "order-1",
            CustomerId: "customer-1",
            Amount: amount,
            Currency: "usd",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: "idem-1",
            CustomerEmail: "customer@example.test");

    [Fact]
    public async Task Handle_ShouldReturnExistingPayment_WhenDuplicateIdempotencyKey()
    {
        var existing = Payment.Create(
            PaymentId.From("pay-existing"),
            "order-1",
            "customer-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-1"),
            FixedNow.AddMinutes(-5));

        existing.MarkSucceeded(ProviderPaymentIntentId.From("pi_existing"), FixedNow.AddMinutes(-4));

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

        await _stripePaymentProvider.DidNotReceive().ProcessPaymentAsync(
            Arg.Any<ProcessPaymentProviderRequest>(),
            Arg.Any<CancellationToken>());

        await _paymentRepository.DidNotReceive().AddAsync(
            Arg.Any<Payment>(),
            Arg.Any<CancellationToken>());

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldPersistPaymentAndReturnSucceeded_WhenProviderSucceeds()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: "pi_123",
                ClientSecret: null,
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
        Assert.Equal("pi_123", result.Value.ProviderPaymentIntentId);

        Assert.NotNull(addedPayment);
        Assert.Equal("order-1", addedPayment!.OrderId);
        Assert.Equal(PaymentStatus.Succeeded, addedPayment.Status);
        Assert.Equal("pi_123", addedPayment.ProviderPaymentIntentId?.Value);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenPendingStatusMissingProviderIntentId()
    {
        _paymentRepository
            .GetByOrderIdAndIdempotencyKeyAsync(
                Arg.Any<string>(),
                Arg.Any<IdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        _stripePaymentProvider
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Pending,
                ProviderPaymentIntentId: null,
                ClientSecret: null,
                ErrorCode: null,
                ErrorMessage: null));

        var result = await BuildHandler().Handle(ValidCommand(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("ProviderPaymentIntentId is required", result.Errors[0]);

        await _paymentRepository.DidNotReceive().AddAsync(
            Arg.Any<Payment>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenDomainValidationFails()
    {
        var result = await BuildHandler().Handle(ValidCommand(amount: 0m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("greater then zero", result.Errors[0], StringComparison.OrdinalIgnoreCase);

        await _stripePaymentProvider.DidNotReceive().ProcessPaymentAsync(
            Arg.Any<ProcessPaymentProviderRequest>(),
            Arg.Any<CancellationToken>());
    }
}