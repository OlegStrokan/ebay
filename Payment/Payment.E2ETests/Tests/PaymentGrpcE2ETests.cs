using FluentAssertions;
using Payment.E2ETests.Infrastructure;
using Protos.Common;
using Protos.Payment;
using Xunit;

namespace Payment.E2ETests.Tests;

[Collection("PaymentE2E")]
public sealed class PaymentGrpcE2ETests : IClassFixture<E2ETestServer>, IAsyncLifetime
{
    private readonly E2ETestServer _server;
    private PaymentService.PaymentServiceClient _client = null!;

    public PaymentGrpcE2ETests(E2ETestServer server)
    {
        _server = server;
    }

    public Task InitializeAsync()
    {
        _server.ResetAll();
        _client = _server.CreatePaymentClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProcessPayment_ThenGetById_ThenGetByOrderAndIdempotency_ShouldReturnSamePayment()
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var customerId = $"customer-{Guid.NewGuid():N}";
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";

        var processRequest = new ProcessPaymentRequest
        {
            OrderId = orderId,
            CustomerId = customerId,
            Amount = ToDecimalValue(49.99m),
            Currency = "USD",
            PaymentMethod = "card",
            IdempotencyKey = idempotencyKey,
        };

        var first = await _client.ProcessPaymentAsync(processRequest);
        var second = await _client.ProcessPaymentAsync(processRequest);

        first.Success.Should().BeTrue();
        first.Status.Should().Be(Protos.Payment.ProcessPaymentStatus.Succeeded);
        first.PaymentId.Should().NotBeNullOrWhiteSpace();
        second.Success.Should().BeTrue();
        second.PaymentId.Should().Be(first.PaymentId, "idempotent retry must return the same payment");

        var byId = await _client.GetPaymentAsync(new GetPaymentRequest { PaymentId = first.PaymentId });
        byId.Success.Should().BeTrue();
        byId.Payment.PaymentId.Should().Be(first.PaymentId);
        byId.Payment.OrderId.Should().Be(orderId);
        byId.Payment.Status.Should().Be(PaymentRecordStatus.Succeeded);

        var byOrder = await _client.GetPaymentByOrderAndIdempotencyAsync(new GetPaymentByOrderAndIdempotencyRequest
        {
            OrderId = orderId,
            IdempotencyKey = idempotencyKey,
        });

        byOrder.Success.Should().BeTrue();
        byOrder.Payment.PaymentId.Should().Be(first.PaymentId);
        byOrder.Payment.OrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task RefundPayment_ShouldMarkPaymentAsRefunded()
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var customerId = $"customer-{Guid.NewGuid():N}";

        var process = await _client.ProcessPaymentAsync(new ProcessPaymentRequest
        {
            OrderId = orderId,
            CustomerId = customerId,
            Amount = ToDecimalValue(30m),
            Currency = "USD",
            PaymentMethod = "card",
            IdempotencyKey = $"process-{Guid.NewGuid():N}",
        });

        process.Success.Should().BeTrue();

        var refund = await _client.RefundPaymentAsync(new RefundPaymentRequest
        {
            PaymentId = process.PaymentId,
            // Full amount: a partial refund deliberately leaves the payment in Succeeded.
            Amount = ToDecimalValue(30m),
            Currency = "USD",
            Reason = "requested_by_customer",
            IdempotencyKey = $"refund-{Guid.NewGuid():N}",
        });

        refund.Success.Should().BeTrue();
        refund.Status.Should().Be(Protos.Payment.RefundPaymentStatus.Succeeded);
        refund.PaymentId.Should().Be(process.PaymentId);
        refund.RefundId.Should().NotBeNullOrWhiteSpace();

        var byId = await _client.GetPaymentAsync(new GetPaymentRequest { PaymentId = process.PaymentId });
        byId.Success.Should().BeTrue();
        byId.Payment.Status.Should().Be(PaymentRecordStatus.Refunded);
    }

    private static DecimalValue ToDecimalValue(decimal value)
    {
        var units = decimal.ToInt64(decimal.Truncate(value));
        var nanos = decimal.ToInt32(decimal.Round((value - units) * 1_000_000_000m));
        return new DecimalValue { Units = units, Nanos = nanos };
    }
}
