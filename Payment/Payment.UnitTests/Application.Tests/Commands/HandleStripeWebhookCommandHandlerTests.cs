using Application.Commands.HandleStripeWebhook;
using Application.DTOs;
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

public class HandleStripeWebhookCommandHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 24, 16, 0, 0, DateTimeKind.Utc);

    private readonly IPaymentWebhookEventRepository _webhookRepository = Substitute.For<IPaymentWebhookEventRepository>();
    private readonly IPaymentRepository _paymentRepository = Substitute.For<IPaymentRepository>();
    private readonly IRefundRepository _refundRepository = Substitute.For<IRefundRepository>();
    private readonly IOrderCallbackQueueService _queueService = Substitute.For<IOrderCallbackQueueService>();
    private readonly IMoneyEventQueueService _moneyEventQueueService = Substitute.For<IMoneyEventQueueService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<HandleStripeWebhookCommandHandler> _logger = NullLogger<HandleStripeWebhookCommandHandler>.Instance;

    public HandleStripeWebhookCommandHandlerTests()
    {
        _clock.UtcNow.Returns(FixedNow);
    }

    private HandleStripeWebhookCommandHandler BuildHandler() =>
        new(_webhookRepository, _paymentRepository, _refundRepository, _queueService, _moneyEventQueueService, _unitOfWork, _clock, _logger);

    private static Payment CreatePendingPayment()
    {
        var payment = Payment.Create(
            PaymentId.From("pay-1"),
            "order-1",
            "customer-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-1"),
            FixedNow.AddMinutes(-20));
        payment.MarkPendingProviderConfirmation(ProviderPaymentIntentId.From("pi_1"), FixedNow.AddMinutes(-19));
        return payment;
    }

    [Fact]
    public async Task Handle_ShouldReturnDuplicateResult_WhenProviderEventAlreadyProcessed()
    {
        _webhookRepository.GetByProviderEventIdAsync("evt-1", Arg.Any<CancellationToken>())
            .Returns(PaymentWebhookEvent.Create("evt-1", "payment_intent.succeeded", "{}", FixedNow.AddMinutes(-1)));

        var command = new HandleStripeWebhookCommand("evt-1", "payment_intent.succeeded", "{}", StripeWebhookOutcome.PaymentSucceeded, "pay-1", null, null, null, null);
        var result = await BuildHandler().Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.IsDuplicate);
        Assert.True(result.Value.IsIgnored);

        await _webhookRepository.DidNotReceive().AddAsync(Arg.Any<PaymentWebhookEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldMarkUnknownOutcomeAsProcessed()
    {
        _webhookRepository.GetByProviderEventIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PaymentWebhookEvent?)null);

        var command = new HandleStripeWebhookCommand("evt-2", "unknown", "{}", StripeWebhookOutcome.Unknown, null, null, null, null, null);
        var result = await BuildHandler().Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.Processed);
        Assert.True(result.Value.IsIgnored);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenPaymentCannotBeResolved()
    {
        _webhookRepository.GetByProviderEventIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PaymentWebhookEvent?)null);
        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns((Payment?)null);

        var command = new HandleStripeWebhookCommand("evt-3", "payment_intent.succeeded", "{}", StripeWebhookOutcome.PaymentSucceeded, "pay-404", null, null, null, null);
        var result = await BuildHandler().Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("could not be resolved", result.Errors[0], StringComparison.OrdinalIgnoreCase);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldMarkPaymentSucceeded_AndQueueCallback()
    {
        var payment = CreatePendingPayment();

        _webhookRepository.GetByProviderEventIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PaymentWebhookEvent?)null);
        _paymentRepository.GetByIdAsync(Arg.Any<PaymentId>(), Arg.Any<CancellationToken>())
            .Returns(payment);
        _queueService.QueuePaymentSucceededAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())
            .Returns(new OrderCallbackQueuedDto("evt-out", payment.Id.Value, "PaymentSucceededEvent", payment.OrderId, FixedNow));

        var command = new HandleStripeWebhookCommand(
            "evt-4",
            "payment_intent.succeeded",
            "{}",
            StripeWebhookOutcome.PaymentSucceeded,
            "pay-1",
            "pi_1",
            null,
            null,
            null);

        var result = await BuildHandler().Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(payment.Id.Value, result.Value!.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);

        await _paymentRepository.Received(1).UpdateAsync(payment, Arg.Any<CancellationToken>());
        await _queueService.Received(1).QueuePaymentSucceededAsync(payment, Arg.Any<CancellationToken>());
    }
}
