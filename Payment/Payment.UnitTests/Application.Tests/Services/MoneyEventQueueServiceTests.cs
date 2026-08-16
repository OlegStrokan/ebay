using Application.Gateways;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using NSubstitute;

namespace Application.Tests.Services;

public class MoneyEventQueueServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly IOutboundMoneyEventRepository _repository =
        Substitute.For<IOutboundMoneyEventRepository>();

    private readonly IMoneyEventPayloadSerializer _serializer =
        Substitute.For<IMoneyEventPayloadSerializer>();

    private readonly IClock _clock = Substitute.For<IClock>();

    public MoneyEventQueueServiceTests()
    {
        _clock.UtcNow.Returns(FixedNow);
        _serializer.SerializePaymentAuthorized(Arg.Any<string>(), Arg.Any<Payment>(), Arg.Any<DateTime>())
            .Returns("{}");
        _serializer.SerializePaymentVoided(Arg.Any<string>(), Arg.Any<Payment>(), Arg.Any<DateTime>())
            .Returns("{}");
        _serializer.SerializePaymentCaptured(Arg.Any<string>(), Arg.Any<Payment>(), Arg.Any<DateTime>())
            .Returns("{}");
        _serializer.SerializeRefundIssued(Arg.Any<string>(), Arg.Any<Payment>(), Arg.Any<Refund>(), Arg.Any<DateTime>())
            .Returns("{}");
    }

    private MoneyEventQueueService BuildService() => new(_repository, _serializer, _clock);

    private static Payment CreatePayment()
    {
        var payment = Payment.Create(
            PaymentId.From("pay-money-1"),
            "order-money-1",
            "customer-money-1",
            Money.Create(100m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-money-1"),
            FixedNow.AddMinutes(-10));

        payment.MarkSucceeded(ProviderPaymentIntentId.From("pi_money_1"), FixedNow.AddMinutes(-9));
        return payment;
    }

    [Theory]
    [InlineData("authorized")]
    [InlineData("voided")]
    [InlineData("captured")]
    public async Task Queue_ShouldAddOneOutboxRowKeyedOnThePaymentAndLeg(string leg)
    {
        var payment = CreatePayment();
        var service = BuildService();

        var dto = leg switch
        {
            "authorized" => await service.QueuePaymentAuthorizedAsync(payment, CancellationToken.None),
            "voided" => await service.QueuePaymentVoidedAsync(payment, CancellationToken.None),
            _ => await service.QueuePaymentCapturedAsync(payment, CancellationToken.None),
        };

        Assert.Equal($"pay-money-1:{leg}", dto.EventId);
        Assert.Equal("order-money-1", dto.OrderId);

        await _repository.Received(1).AddAsync(
            Arg.Is<OutboundMoneyEvent>(e =>
                e.EventId == $"pay-money-1:{leg}"
                && e.PaymentId == "pay-money-1"
                && e.OrderId == "order-money-1"
                && e.Status == CallbackDeliveryStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueRefundIssuedAsync_ShouldKeyOnTheRefundNotTheAmount()
    {
        // An order can be refunded more than once, so keying on the amount would make a second
        // refund of the same value look like a retry and drop it from the ledger.
        var payment = CreatePayment();
        var refund = Refund.Create(
            payment.Id,
            Money.Create(40m, "USD"),
            "requested_by_customer",
            IdempotencyKey.From("refund-idem-1"),
            FixedNow);

        var dto = await BuildService().QueueRefundIssuedAsync(payment, refund, CancellationToken.None);

        Assert.Equal($"{refund.Id.Value}:refunded", dto.EventId);

        await _repository.Received(1).AddAsync(
            Arg.Is<OutboundMoneyEvent>(e => e.EventId == $"{refund.Id.Value}:refunded"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Queue_ShouldNotAddSecondRow_WhenTheSameLegIsQueuedAgain()
    {
        // Capture, webhook and reconciliation can all resolve the same payment.
        var payment = CreatePayment();

        _repository.GetByEventIdAsync("pay-money-1:captured", Arg.Any<CancellationToken>())
            .Returns(OutboundMoneyEvent.Create(
                "pay-money-1:captured",
                "pay-money-1",
                "order-money-1",
                "PaymentCapturedEvent",
                "{}",
                FixedNow.AddMinutes(-1)));

        var dto = await BuildService().QueuePaymentCapturedAsync(payment, CancellationToken.None);

        Assert.Equal("pay-money-1:captured", dto.EventId);
        Assert.Equal(FixedNow.AddMinutes(-1), dto.QueuedAt);

        await _repository.DidNotReceive().AddAsync(
            Arg.Any<OutboundMoneyEvent>(),
            Arg.Any<CancellationToken>());
    }
}
