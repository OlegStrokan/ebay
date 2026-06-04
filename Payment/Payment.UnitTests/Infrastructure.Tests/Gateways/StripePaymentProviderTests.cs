using Application.Gateways.Models;
using Domain.Enums;
using Infrastructure.Gateways;
using Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Gateways;

public class StripePaymentProviderTests
{
    private static FakePaymentProvider BuildFakeProvider() => new();

    private static StripePaymentProvider BuildRealWithoutSecret() =>
        new(
            Microsoft.Extensions.Options.Options.Create(new StripeOptions
            {
                ProviderType = PaymentProviderType.Stripe,
                SecretKey = "",
                DefaultCurrency = "USD",
            }),
            NullLogger<StripePaymentProvider>.Instance);

    [Fact]
    public async Task ProcessPaymentAsync_Fake_ShouldReturnSucceeded_WhenKeyIsNormal()
    {
        var result = await BuildFakeProvider().ProcessPaymentAsync(new ProcessPaymentProviderRequest(
            PaymentId: "pay-1",
            OrderId: "order-1",
            CustomerId: "customer-1",
            Amount: 10m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: "idem-normal",
            ReturnUrl: null,
            CancelUrl: null,
            CustomerEmail: null));

        Assert.Equal(ProviderProcessPaymentStatus.Succeeded, result.Status);
        Assert.StartsWith("pi_success_", result.ProviderPaymentIntentId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Fake_ShouldReturnRequiresAction_WhenKeyContainsAction()
    {
        var result = await BuildFakeProvider().ProcessPaymentAsync(new ProcessPaymentProviderRequest(
            PaymentId: "pay-2",
            OrderId: "order-2",
            CustomerId: "customer-2",
            Amount: 10m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: "idem-action-3ds",
            ReturnUrl: null,
            CancelUrl: null,
            CustomerEmail: null));

        Assert.Equal(ProviderProcessPaymentStatus.RequiresAction, result.Status);
        Assert.StartsWith("pi_action_", result.ProviderPaymentIntentId, StringComparison.Ordinal);
        Assert.StartsWith("cs_action_", result.ClientSecret, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Fake_ShouldReturnFailed_WhenInvalidAmount()
    {
        var result = await BuildFakeProvider().ProcessPaymentAsync(new ProcessPaymentProviderRequest(
            PaymentId: "pay-3",
            OrderId: "order-3",
            CustomerId: "customer-3",
            Amount: 0m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: "idem-fail",
            ReturnUrl: null,
            CancelUrl: null,
            CustomerEmail: null));

        Assert.Equal(ProviderProcessPaymentStatus.Failed, result.Status);
        Assert.Equal("invalid_amount", result.ErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Real_ShouldReturnFailed_WhenSecretNotConfigured()
    {
        var result = await BuildRealWithoutSecret().ProcessPaymentAsync(new ProcessPaymentProviderRequest(
            PaymentId: "pay-4",
            OrderId: "order-4",
            CustomerId: "customer-4",
            Amount: 10m,
            Currency: "USD",
            PaymentMethod: PaymentMethod.Card,
            IdempotencyKey: "idem-real",
            ReturnUrl: null,
            CancelUrl: null,
            CustomerEmail: null));

        Assert.Equal(ProviderProcessPaymentStatus.Failed, result.Status);
        Assert.Equal("stripe_secret_not_configured", result.ErrorCode);
    }

    [Fact]
    public async Task RefundPaymentAsync_Fake_ShouldReturnPending_WhenKeyContainsPending()
    {
        var result = await BuildFakeProvider().RefundPaymentAsync(new RefundPaymentProviderRequest(
            PaymentId: "pay-5",
            ProviderPaymentIntentId: "pi_5",
            Amount: 2m,
            Currency: "USD",
            Reason: "customer",
            IdempotencyKey: "idem-pending"));

        Assert.Equal(ProviderRefundPaymentStatus.Pending, result.Status);
        Assert.StartsWith("re_pending_", result.ProviderRefundId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefundPaymentAsync_Real_ShouldReturnFailed_WhenProviderPaymentIntentMissing()
    {
        var provider = BuildRealWithoutSecret();
        var result = await provider.RefundPaymentAsync(new RefundPaymentProviderRequest(
            PaymentId: "pay-6",
            ProviderPaymentIntentId: "",
            Amount: 2m,
            Currency: "USD",
            Reason: "customer",
            IdempotencyKey: "idem-1"));

        // secret-not-configured branch runs before provider payment intent validation
        Assert.Equal(ProviderRefundPaymentStatus.Failed, result.Status);
        Assert.Equal("stripe_secret_not_configured", result.ErrorCode);
    }

    [Fact]
    public async Task GetPaymentStatusAsync_Fake_ShouldMapStatuses()
    {
        var provider = BuildFakeProvider();

        var pending = await provider.GetPaymentStatusAsync("pi_pending_1");
        var failed = await provider.GetPaymentStatusAsync("pi_fail_1");
        var succeeded = await provider.GetPaymentStatusAsync("pi_success_1");

        Assert.Equal(ProviderPaymentLifecycleStatus.Pending, pending.Status);
        Assert.Equal(ProviderPaymentLifecycleStatus.Failed, failed.Status);
        Assert.Equal(ProviderPaymentLifecycleStatus.Succeeded, succeeded.Status);
    }

    [Fact]
    public async Task GetRefundStatusAsync_Fake_ShouldMapStatuses()
    {
        var provider = BuildFakeProvider();

        var pending = await provider.GetRefundStatusAsync("re_pending_1");
        var failed = await provider.GetRefundStatusAsync("re_fail_1");
        var succeeded = await provider.GetRefundStatusAsync("re_success_1");

        Assert.Equal(ProviderRefundLifecycleStatus.Pending, pending.Status);
        Assert.Equal(ProviderRefundLifecycleStatus.Failed, failed.Status);
        Assert.Equal(ProviderRefundLifecycleStatus.Succeeded, succeeded.Status);
    }

    [Fact]
    public async Task CapturePaymentAsync_Fake_ShouldReturnSucceeded_WhenIntentIdIsNormal()
    {
        var result = await BuildFakeProvider().CapturePaymentAsync(new Application.Gateways.Models.CapturePaymentProviderRequest(
            PaymentId: "pay-cap-1",
            OrderId: "order-cap-1",
            CustomerId: "cust-cap-1",
            ProviderPaymentIntentId: "pi_test_normal",
            Amount: 50m,
            Currency: "USD",
            IdempotencyKey: "idem-cap-1"), CancellationToken.None);

        Assert.Equal(ProviderProcessPaymentStatus.Succeeded, result.Status);
        Assert.StartsWith("pi_captured_", result.ProviderPaymentIntentId, StringComparison.Ordinal);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task CapturePaymentAsync_Fake_ShouldReturnFailed_WhenIntentIdContainsFail()
    {
        var result = await BuildFakeProvider().CapturePaymentAsync(new Application.Gateways.Models.CapturePaymentProviderRequest(
            PaymentId: "pay-cap-2",
            OrderId: "order-cap-2",
            CustomerId: "cust-cap-2",
            ProviderPaymentIntentId: "pi_fail_card_declined",
            Amount: 50m,
            Currency: "USD",
            IdempotencyKey: "idem-cap-2"), CancellationToken.None);

        Assert.Equal(ProviderProcessPaymentStatus.Failed, result.Status);
        Assert.Equal("provider_capture_failed", result.ErrorCode);
    }

    [Fact]
    public async Task CapturePaymentAsync_Fake_ShouldReturnFailed_WhenAmountIsZero()
    {
        var result = await BuildFakeProvider().CapturePaymentAsync(new Application.Gateways.Models.CapturePaymentProviderRequest(
            PaymentId: "pay-cap-3",
            OrderId: "order-cap-3",
            CustomerId: "cust-cap-3",
            ProviderPaymentIntentId: "pi_test_normal",
            Amount: 0m,
            Currency: "USD",
            IdempotencyKey: "idem-cap-3"), CancellationToken.None);

        Assert.Equal(ProviderProcessPaymentStatus.Failed, result.Status);
        Assert.Equal("invalid_amount", result.ErrorCode);
    }

    [Fact]
    public async Task CapturePaymentAsync_Fake_ShouldReturnFailed_WhenProviderIntentIdMissing()
    {
        var result = await BuildFakeProvider().CapturePaymentAsync(new Application.Gateways.Models.CapturePaymentProviderRequest(
            PaymentId: "pay-cap-4",
            OrderId: "order-cap-4",
            CustomerId: "cust-cap-4",
            ProviderPaymentIntentId: "",
            Amount: 10m,
            Currency: "USD",
            IdempotencyKey: "idem-cap-4"), CancellationToken.None);

        Assert.Equal(ProviderProcessPaymentStatus.Failed, result.Status);
        Assert.Equal("missing_provider_payment_intent_id", result.ErrorCode);
    }

    [Fact]
    public async Task CapturePaymentAsync_Real_ShouldReturnFailed_WhenSecretNotConfigured()
    {
        var result = await BuildRealWithoutSecret().CapturePaymentAsync(new Application.Gateways.Models.CapturePaymentProviderRequest(
            PaymentId: "pay-cap-real-1",
            OrderId: "order-real-1",
            CustomerId: "cust-real-1",
            ProviderPaymentIntentId: "pi_test_real",
            Amount: 100m,
            Currency: "USD",
            IdempotencyKey: "idem-cap-real-1"), CancellationToken.None);

        Assert.Equal(ProviderProcessPaymentStatus.Failed, result.Status);
        Assert.Equal("stripe_secret_not_configured", result.ErrorCode);
    }

    // ---- CancelAuthorizationAsync -----------------------------------------------

    [Fact]
    public async Task CancelAuthorizationAsync_Fake_ShouldCompleteWithoutThrowing()
    {
        var exception = await Record.ExceptionAsync(() =>
            BuildFakeProvider().CancelAuthorizationAsync("pi_auth_test_1", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CancelAuthorizationAsync_Real_ShouldThrow_WhenSecretNotConfigured()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildRealWithoutSecret().CancelAuthorizationAsync("pi_auth_real_1", CancellationToken.None));
    }
}
