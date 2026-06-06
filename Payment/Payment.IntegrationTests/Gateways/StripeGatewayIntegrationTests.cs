using Application.Gateways;
using Application.Gateways.Models;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Payment.IntegrationTests.Infrastructure;
using Xunit;

namespace Payment.IntegrationTests.Gateways;

[Collection("PaymentIntegration")]
public sealed class StripeGatewayIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public StripeGatewayIntegrationTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessPayment_ShouldReturnRequiresAction_ForActionIdempotencyKey()
    {
        await using var scope = _fixture.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStripePaymentProvider>();

        var result = await gateway.ProcessPaymentAsync(new ProcessPaymentProviderRequest(
            PaymentId: $"pay-{Guid.NewGuid():N}",
            OrderId: $"order-{Guid.NewGuid():N}",
            CustomerId: $"customer-{Guid.NewGuid():N}",
            Amount: 10m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: $"action-{Guid.NewGuid():N}",
            CustomerEmail: null));

        Assert.Equal(ProviderProcessPaymentStatus.RequiresAction, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ProviderPaymentIntentId));
        Assert.False(string.IsNullOrWhiteSpace(result.ClientSecret));
    }

    [Fact]
    public async Task RefundPayment_ShouldReturnPending_ForPendingIdempotencyKey()
    {
        await using var scope = _fixture.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IStripePaymentProvider>();

        var result = await gateway.RefundPaymentAsync(new RefundPaymentProviderRequest(
            PaymentId: $"pay-{Guid.NewGuid():N}",
            ProviderPaymentIntentId: $"pi_{Guid.NewGuid():N}",
            Amount: 5m,
            Currency: "USD",
            Reason: "customer request",
            IdempotencyKey: $"pending-{Guid.NewGuid():N}"));

        Assert.Equal(ProviderRefundPaymentStatus.Pending, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ProviderRefundId));
    }
}
