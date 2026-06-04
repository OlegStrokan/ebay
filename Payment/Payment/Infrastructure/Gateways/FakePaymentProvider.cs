using Application.Gateways;
using Application.Gateways.Models;
using Domain.Enums;
using Infrastructure.Options;

namespace Infrastructure.Gateways;

internal sealed class FakePaymentProvider : IStripePaymentProvider
{
    public Task<ProcessPaymentProviderResult> ProcessPaymentAsync(
        ProcessPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(SimulateProcessPayment(request));

    public Task<RefundPaymentProviderResult> RefundPaymentAsync(
        RefundPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(SimulateRefundPayment(request));

    public Task<CapturePaymentProviderResult> CapturePaymentAsync(
        CapturePaymentProviderRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(SimulateCapturePayment(request));

    public Task<ProviderPaymentStatusResult> GetPaymentStatusAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(SimulatePaymentStatus(providerPaymentIntentId));

    public Task<ProviderRefundStatusResult> GetRefundStatusAsync(
        string providerRefundId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(SimulateRefundStatus(providerRefundId));

    public Task CancelAuthorizationAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // -------------------------------------------------------------------------
    // Simulation helpers
    // -------------------------------------------------------------------------

    private static ProcessPaymentProviderResult SimulateProcessPayment(ProcessPaymentProviderRequest request)
    {
        if (request.Amount <= 0)
        {
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ClientSecret: null,
                ErrorCode: "invalid_amount",
                ErrorMessage: "Payment amount must be greater than zero.");
        }

        if (request.PaymentMethod == PaymentMethod.Unknown)
        {
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ClientSecret: null,
                ErrorCode: "invalid_payment_method",
                ErrorMessage: "Unknown payment method is not supported by provider.");
        }

        var loweredKey = request.IdempotencyKey.Trim().ToLowerInvariant();
        var token = BuildStableToken(request.PaymentId, request.IdempotencyKey);

        if (loweredKey.Contains("fail", StringComparison.Ordinal))
        {
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: $"pi_fail_{token}",
                ClientSecret: null,
                ErrorCode: "provider_payment_failed",
                ErrorMessage: "Simulated provider failure.");
        }

        if (loweredKey.Contains("action", StringComparison.Ordinal)
            || loweredKey.Contains("3ds", StringComparison.Ordinal))
        {
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.RequiresAction,
                ProviderPaymentIntentId: $"pi_action_{token}",
                ClientSecret: $"cs_action_{token}",
                ErrorCode: null,
                ErrorMessage: null);
        }

        if (loweredKey.Contains("pending", StringComparison.Ordinal))
        {
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Pending,
                ProviderPaymentIntentId: $"pi_pending_{token}",
                ClientSecret: $"cs_pending_{token}",
                ErrorCode: null,
                ErrorMessage: null);
        }

        return new ProcessPaymentProviderResult(
            Status: ProviderProcessPaymentStatus.Succeeded,
            ProviderPaymentIntentId: $"pi_success_{token}",
            ClientSecret: null,
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static CapturePaymentProviderResult SimulateCapturePayment(CapturePaymentProviderRequest request)
    {
        if (request.Amount <= 0)
        {
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "invalid_amount",
                ErrorMessage: "Capture amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "missing_provider_payment_intent_id",
                ErrorMessage: "ProviderPaymentIntentId is required for capture.");
        }

        var token = BuildStableToken(request.PaymentId, request.IdempotencyKey);
        var loweredIntentId = request.ProviderPaymentIntentId.Trim().ToLowerInvariant();

        if (loweredIntentId.Contains("fail", StringComparison.Ordinal))
        {
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: request.ProviderPaymentIntentId,
                ErrorCode: "provider_capture_failed",
                ErrorMessage: "Simulated capture failure.");
        }

        return new CapturePaymentProviderResult(
            Status: ProviderProcessPaymentStatus.Succeeded,
            ProviderPaymentIntentId: $"pi_captured_{token}",
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static RefundPaymentProviderResult SimulateRefundPayment(RefundPaymentProviderRequest request)
    {
        if (request.Amount <= 0)
        {
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: null,
                ErrorCode: "invalid_amount",
                ErrorMessage: "Refund amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: null,
                ErrorCode: "missing_provider_payment_intent_id",
                ErrorMessage: "Provider payment intent id is required for refunds.");
        }

        var loweredKey = request.IdempotencyKey.Trim().ToLowerInvariant();
        var token = BuildStableToken(request.PaymentId, request.IdempotencyKey);

        if (loweredKey.Contains("fail", StringComparison.Ordinal))
        {
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: $"re_fail_{token}",
                ErrorCode: "provider_refund_failed",
                ErrorMessage: "Simulated provider refund failure.");
        }

        if (loweredKey.Contains("pending", StringComparison.Ordinal))
        {
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Pending,
                ProviderRefundId: $"re_pending_{token}",
                ErrorCode: null,
                ErrorMessage: null);
        }

        return new RefundPaymentProviderResult(
            Status: ProviderRefundPaymentStatus.Succeeded,
            ProviderRefundId: $"re_success_{token}",
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static ProviderPaymentStatusResult SimulatePaymentStatus(string providerPaymentIntentId)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Unknown,
                ErrorCode: "missing_provider_payment_intent_id",
                ErrorMessage: "Provider payment intent id is required.");
        }

        var loweredId = providerPaymentIntentId.Trim().ToLowerInvariant();

        if (loweredId.Contains("fail", StringComparison.Ordinal))
        {
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Failed,
                ErrorCode: "provider_marked_failed",
                ErrorMessage: "Provider marked payment as failed.");
        }

        if (loweredId.Contains("pending", StringComparison.Ordinal)
            || loweredId.Contains("action", StringComparison.Ordinal))
        {
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Pending,
                ErrorCode: null,
                ErrorMessage: null);
        }

        return new ProviderPaymentStatusResult(
            Status: ProviderPaymentLifecycleStatus.Succeeded,
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static ProviderRefundStatusResult SimulateRefundStatus(string providerRefundId)
    {
        if (string.IsNullOrWhiteSpace(providerRefundId))
        {
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Unknown,
                ErrorCode: "missing_provider_refund_id",
                ErrorMessage: "Provider refund id is required.");
        }

        var loweredId = providerRefundId.Trim().ToLowerInvariant();

        if (loweredId.Contains("fail", StringComparison.Ordinal))
        {
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Failed,
                ErrorCode: "provider_marked_failed",
                ErrorMessage: "Provider marked refund as failed.");
        }

        if (loweredId.Contains("pending", StringComparison.Ordinal))
        {
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Pending,
                ErrorCode: null,
                ErrorMessage: null);
        }

        return new ProviderRefundStatusResult(
            Status: ProviderRefundLifecycleStatus.Succeeded,
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static string BuildStableToken(string paymentId, string idempotencyKey)
    {
        var raw = $"{paymentId.Trim()}|{idempotencyKey.Trim()}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
