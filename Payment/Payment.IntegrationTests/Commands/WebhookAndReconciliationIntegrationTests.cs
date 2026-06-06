using Application.Commands.HandleStripeWebhook;
using Application.Commands.ProcessPayment;
using Application.Commands.ReconcilePendingPayments;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using Infrastructure.Persistence.DbContext;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payment.IntegrationTests.Infrastructure;
using Xunit;

namespace Payment.IntegrationTests.Commands;

[Collection("PaymentIntegration")]
public sealed class WebhookAndReconciliationIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public WebhookAndReconciliationIntegrationTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HandleStripeWebhook_ShouldUpdatePayment_PersistWebhook_AndQueueCallback()
    {
        await using var scope = _fixture.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var paymentRepository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var webhookRepository = scope.ServiceProvider.GetRequiredService<IPaymentWebhookEventRepository>();
        var callbackRepository = scope.ServiceProvider.GetRequiredService<IOutboundOrderCallbackRepository>();

        var process = await mediator.Send(new ProcessPaymentCommand(
            OrderId: $"order-{Guid.NewGuid():N}",
            CustomerId: $"customer-{Guid.NewGuid():N}",
            Amount: 55m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: $"pending-{Guid.NewGuid():N}",
            CustomerEmail: null));

        Assert.True(process.IsSuccess);
        var payment = await paymentRepository.GetByIdAsync(PaymentId.From(process.Value!.PaymentId));
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.PendingProviderConfirmation, payment!.Status);

        var webhookResult = await mediator.Send(new HandleStripeWebhookCommand(
            ProviderEventId: $"evt-{Guid.NewGuid():N}",
            EventType: "payment_intent.succeeded",
            PayloadJson: "{}",
            Outcome: StripeWebhookOutcome.PaymentSucceeded,
            PaymentId: payment.Id.Value,
            ProviderPaymentIntentId: payment.ProviderPaymentIntentId?.Value,
            ProviderRefundId: null,
            FailureCode: null,
            FailureMessage: null));

        Assert.True(webhookResult.IsSuccess);
        Assert.NotNull(webhookResult.Value);
        Assert.True(webhookResult.Value!.Processed);
        Assert.False(webhookResult.Value.IsIgnored);

        var updated = await paymentRepository.GetByIdAsync(payment.Id);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);

        var persistedWebhook = await webhookRepository.GetByProviderEventIdAsync(webhookResult.Value.ProviderEventId);
        Assert.NotNull(persistedWebhook);

        var callbacks = await callbackRepository.GetPendingAsync(DateTime.UtcNow.AddMinutes(5), 20);
        Assert.Contains(callbacks, c => c.OrderId == payment.OrderId && c.EventType == "PaymentSucceededEvent");
    }

    [Fact]
    public async Task ReconcilePendingPayments_ShouldProcessPendingPaymentAndQueueCallback()
    {
        await using var scope = _fixture.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var process = await mediator.Send(new ProcessPaymentCommand(
            OrderId: $"order-{Guid.NewGuid():N}",
            CustomerId: $"customer-{Guid.NewGuid():N}",
            Amount: 33m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: $"pending-{Guid.NewGuid():N}",
            CustomerEmail: null));

        Assert.True(process.IsSuccess);
        Assert.NotNull(process.Value);
        Assert.Equal(Application.DTOs.ProcessPaymentStatus.Pending, process.Value!.Status);

        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE payments SET updated_at = NOW() - INTERVAL '2 minutes' WHERE id = {0}",
            process.Value.PaymentId);

        var reconcile = await mediator.Send(new ReconcilePendingPaymentsCommand(
            OlderThanMinutes: 1,
            BatchSize: 50));

        Assert.True(reconcile.IsSuccess);
        Assert.NotNull(reconcile.Value);
        Assert.True(reconcile.Value!.PaymentsChecked >= 1);
    }
}
