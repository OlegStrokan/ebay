using Application.Commands.ProcessPayment;
using Application.Commands.RefundPayment;
using Application.DTOs;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Payment.IntegrationTests.Infrastructure;
using Xunit;

namespace Payment.IntegrationTests.Commands;

[Collection("PaymentIntegration")]
public sealed class ProcessAndRefundCommandIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public ProcessAndRefundCommandIntegrationTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessPayment_ShouldPersistPayment_AndBeIdempotent()
    {
        await using var scope = _fixture.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var paymentRepository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();

        var orderId = $"order-{Guid.NewGuid():N}";
        var customerId = $"customer-{Guid.NewGuid():N}";
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";

        var command = new ProcessPaymentCommand(
            OrderId: orderId,
            CustomerId: customerId,
            Amount: 49.99m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: idempotencyKey,
            CustomerEmail: "integration@test.local");

        var first = await mediator.Send(command);
        var second = await mediator.Send(command);

        Assert.True(first.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.Equal(ProcessPaymentStatus.Succeeded, first.Value!.Status);
        Assert.True(second.IsSuccess);
        Assert.NotNull(second.Value);
        Assert.Equal(first.Value.PaymentId, second.Value!.PaymentId);

        var persisted = await paymentRepository.GetByOrderIdAndIdempotencyKeyAsync(orderId, IdempotencyKey.From(idempotencyKey));
        Assert.NotNull(persisted);
        Assert.Equal(PaymentStatus.Succeeded, persisted!.Status);
    }

    [Fact]
    public async Task RefundPayment_ShouldPersistRefund_AndBeIdempotent()
    {
        await using var scope = _fixture.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var paymentRepository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var refundRepository = scope.ServiceProvider.GetRequiredService<IRefundRepository>();

        var orderId = $"order-{Guid.NewGuid():N}";
        var customerId = $"customer-{Guid.NewGuid():N}";

        var processResult = await mediator.Send(new ProcessPaymentCommand(
            OrderId: orderId,
            CustomerId: customerId,
            Amount: 90m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: $"process-{Guid.NewGuid():N}",
            CustomerEmail: null));

        Assert.True(processResult.IsSuccess);
        var paymentId = processResult.Value!.PaymentId;

        var refundIdem = $"refund-{Guid.NewGuid():N}";
        var refundCommand = new RefundPaymentCommand(
            PaymentId: paymentId,
            Amount: 20m,
            Currency: "USD",
            Reason: "requested_by_customer",
            IdempotencyKey: refundIdem);

        var first = await mediator.Send(refundCommand);
        var second = await mediator.Send(refundCommand);

        Assert.True(first.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.Equal(RefundPaymentStatus.Succeeded, first.Value!.Status);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.RefundId, second.Value!.RefundId);

        var payment = await paymentRepository.GetByIdAsync(PaymentId.From(paymentId));
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Refunded, payment!.Status);

        var refund = await refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(PaymentId.From(paymentId), IdempotencyKey.From(refundIdem));
        Assert.NotNull(refund);
        Assert.Equal(RefundStatus.Succeeded, refund!.Status);
    }
}
