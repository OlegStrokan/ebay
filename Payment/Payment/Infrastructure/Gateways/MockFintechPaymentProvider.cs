using System.Net.Http.Json;
using System.Text.Json;
using Application.Gateways;
using Application.Gateways.Models;

namespace Infrastructure.Gateways;

/// <summary>
/// Talks to the standalone "fintech sandbox" payment provider over REST.
/// The sandbox is a separate, independently-owned application (see
/// <c>/partners/fintech-sandbox</c>) that imitates a Stripe-level processor for
/// testing without moving real money. This class is a thin HTTP client; all
/// payment behaviour lives in the sandbox.
///
/// Async outcomes (pending / requires_action) are finalized by signed webhooks
/// the sandbox POSTs back to the existing <c>/api/v1/webhooks/stripe</c>
/// endpoint, with the reconciliation worker polling the status endpoints as a
/// fallback.
/// </summary>
internal sealed class MockFintechPaymentProvider(
    HttpClient httpClient,
    ILogger<MockFintechPaymentProvider> logger) : IStripePaymentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ProcessPaymentProviderResult> ProcessPaymentAsync(
        ProcessPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            payment_id = request.PaymentId,
            order_id = request.OrderId,
            customer_id = request.CustomerId,
            amount_minor = ToMinorUnits(request.Amount),
            currency = request.Currency,
            payment_method = request.PaymentMethod.ToString(),
            idempotency_key = request.IdempotencyKey,
            customer_email = request.CustomerEmail,
            capture_method = request.ManualCapture ? "manual" : "automatic",
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync("/v1/payment-intents", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(response, cancellationToken);
                logger.LogWarning(
                    "MockFintech process payment returned {StatusCode}. PaymentId={PaymentId}, Code={Code}",
                    (int)response.StatusCode, request.PaymentId, code);
                return new ProcessPaymentProviderResult(
                    ProviderProcessPaymentStatus.Failed, null, null,
                    code ?? "provider_error", message ?? "MockFintech process payment failed.");
            }

            var body = await response.Content.ReadFromJsonAsync<PaymentIntentResponse>(JsonOptions, cancellationToken);
            if (body is null)
            {
                return new ProcessPaymentProviderResult(
                    ProviderProcessPaymentStatus.Failed, null, null,
                    "empty_provider_response", "MockFintech returned an empty response.");
            }

            return new ProcessPaymentProviderResult(
                MapProcessStatus(body.Status),
                body.Id,
                body.ClientSecret,
                body.ErrorCode,
                body.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected MockFintech process payment error. PaymentId={PaymentId}", request.PaymentId);
            return new ProcessPaymentProviderResult(
                ProviderProcessPaymentStatus.Failed, null, null,
                "unexpected_provider_error", "Unexpected error calling MockFintech process payment.");
        }
    }

    public async Task<CapturePaymentProviderResult> CapturePaymentAsync(
        CapturePaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new CapturePaymentProviderResult(
                ProviderProcessPaymentStatus.Failed, null,
                "missing_provider_payment_intent_id", "ProviderPaymentIntentId is required for capture.");
        }

        var payload = new
        {
            payment_id = request.PaymentId,
            order_id = request.OrderId,
            customer_id = request.CustomerId,
            amount_minor = ToMinorUnits(request.Amount),
            currency = request.Currency,
            idempotency_key = request.IdempotencyKey,
        };

        var url = $"/v1/payment-intents/{Uri.EscapeDataString(request.ProviderPaymentIntentId)}/capture";

        try
        {
            using var response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(response, cancellationToken);
                return new CapturePaymentProviderResult(
                    ProviderProcessPaymentStatus.Failed, request.ProviderPaymentIntentId,
                    code ?? "capture_failed", message ?? "MockFintech capture failed.");
            }

            var body = await response.Content.ReadFromJsonAsync<PaymentIntentResponse>(JsonOptions, cancellationToken);
            if (body is null)
            {
                return new CapturePaymentProviderResult(
                    ProviderProcessPaymentStatus.Failed, request.ProviderPaymentIntentId,
                    "empty_provider_response", "MockFintech returned an empty response.");
            }

            return new CapturePaymentProviderResult(
                MapProcessStatus(body.Status),
                body.Id ?? request.ProviderPaymentIntentId,
                body.ErrorCode,
                body.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected MockFintech capture error. ProviderPaymentIntentId={Id}", request.ProviderPaymentIntentId);
            return new CapturePaymentProviderResult(
                ProviderProcessPaymentStatus.Failed, null,
                "unexpected_provider_error", "Unexpected error calling MockFintech capture.");
        }
    }

    public async Task CancelAuthorizationAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            throw new ArgumentException("ProviderPaymentIntentId is required for cancel.", nameof(providerPaymentIntentId));
        }

        var url = $"/v1/payment-intents/{Uri.EscapeDataString(providerPaymentIntentId)}/cancel";

        using var response = await httpClient.PostAsync(url, content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var (code, message) = await ReadErrorAsync(response, cancellationToken);
            logger.LogWarning(
                "MockFintech cancel authorization failed. ProviderPaymentIntentId={Id}, Code={Code}",
                providerPaymentIntentId, code);
            throw new InvalidOperationException(message ?? $"MockFintech cancel failed ({(int)response.StatusCode}).");
        }
    }

    public async Task<RefundPaymentProviderResult> RefundPaymentAsync(
        RefundPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new RefundPaymentProviderResult(
                ProviderRefundPaymentStatus.Failed, null,
                "missing_provider_payment_intent_id", "Provider payment intent id is required for refunds.");
        }

        var payload = new
        {
            payment_id = request.PaymentId,
            payment_intent_id = request.ProviderPaymentIntentId,
            amount_minor = ToMinorUnits(request.Amount),
            currency = request.Currency,
            reason = request.Reason,
            idempotency_key = request.IdempotencyKey,
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync("/v1/refunds", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(response, cancellationToken);
                logger.LogWarning(
                    "MockFintech refund returned {StatusCode}. PaymentId={PaymentId}, Code={Code}",
                    (int)response.StatusCode, request.PaymentId, code);
                return new RefundPaymentProviderResult(
                    ProviderRefundPaymentStatus.Failed, null,
                    code ?? "refund_failed", message ?? "MockFintech refund failed.");
            }

            var body = await response.Content.ReadFromJsonAsync<PaymentIntentResponse>(JsonOptions, cancellationToken);
            if (body is null)
            {
                return new RefundPaymentProviderResult(
                    ProviderRefundPaymentStatus.Failed, null,
                    "empty_provider_response", "MockFintech returned an empty response.");
            }

            return new RefundPaymentProviderResult(
                MapRefundStatus(body.Status),
                body.Id,
                body.ErrorCode,
                body.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected MockFintech refund error. PaymentId={PaymentId}", request.PaymentId);
            return new RefundPaymentProviderResult(
                ProviderRefundPaymentStatus.Failed, null,
                "unexpected_provider_error", "Unexpected error calling MockFintech refund.");
        }
    }

    public async Task<ProviderPaymentStatusResult> GetPaymentStatusAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            return new ProviderPaymentStatusResult(
                ProviderPaymentLifecycleStatus.Unknown,
                "missing_provider_payment_intent_id", "Provider payment intent id is required.");
        }

        var url = $"/v1/payment-intents/{Uri.EscapeDataString(providerPaymentIntentId)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Unknown, null, null);
            }

            var body = await response.Content.ReadFromJsonAsync<StatusResponse>(JsonOptions, cancellationToken);
            return new ProviderPaymentStatusResult(
                MapPaymentLifecycle(body?.Status),
                body?.ErrorCode,
                body?.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MockFintech get payment status failed. ProviderPaymentIntentId={Id}", providerPaymentIntentId);
            return new ProviderPaymentStatusResult(ProviderPaymentLifecycleStatus.Unknown, null, null);
        }
    }

    public async Task<ProviderRefundStatusResult> GetRefundStatusAsync(
        string providerRefundId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerRefundId))
        {
            return new ProviderRefundStatusResult(
                ProviderRefundLifecycleStatus.Unknown,
                "missing_provider_refund_id", "Provider refund id is required.");
        }

        var url = $"/v1/refunds/{Uri.EscapeDataString(providerRefundId)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderRefundStatusResult(ProviderRefundLifecycleStatus.Unknown, null, null);
            }

            var body = await response.Content.ReadFromJsonAsync<StatusResponse>(JsonOptions, cancellationToken);
            return new ProviderRefundStatusResult(
                MapRefundLifecycle(body?.Status),
                body?.ErrorCode,
                body?.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MockFintech get refund status failed. ProviderRefundId={Id}", providerRefundId);
            return new ProviderRefundStatusResult(ProviderRefundLifecycleStatus.Unknown, null, null);
        }
    }

    // -------------------------------------------------------------------------
    // Mapping helpers
    // -------------------------------------------------------------------------

    private static ProviderProcessPaymentStatus MapProcessStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" => ProviderProcessPaymentStatus.Succeeded,
        "failed" => ProviderProcessPaymentStatus.Failed,
        "requires_action" => ProviderProcessPaymentStatus.RequiresAction,
        "requires_capture" => ProviderProcessPaymentStatus.RequiresCapture,
        "pending" => ProviderProcessPaymentStatus.Pending,
        _ => ProviderProcessPaymentStatus.Pending,
    };

    private static ProviderRefundPaymentStatus MapRefundStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" => ProviderRefundPaymentStatus.Succeeded,
        "failed" => ProviderRefundPaymentStatus.Failed,
        "pending" => ProviderRefundPaymentStatus.Pending,
        _ => ProviderRefundPaymentStatus.Pending,
    };

    private static ProviderPaymentLifecycleStatus MapPaymentLifecycle(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" => ProviderPaymentLifecycleStatus.Succeeded,
        "failed" => ProviderPaymentLifecycleStatus.Failed,
        "pending" => ProviderPaymentLifecycleStatus.Pending,
        _ => ProviderPaymentLifecycleStatus.Unknown,
    };

    private static ProviderRefundLifecycleStatus MapRefundLifecycle(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" => ProviderRefundLifecycleStatus.Succeeded,
        "failed" => ProviderRefundLifecycleStatus.Failed,
        "pending" => ProviderRefundLifecycleStatus.Pending,
        _ => ProviderRefundLifecycleStatus.Unknown,
    };

    private static long ToMinorUnits(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private async Task<(string? Code, string? Message)> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken);
            return (error?.ErrorCode, error?.ErrorMessage);
        }
        catch
        {
            return (null, null);
        }
    }

    // -------------------------------------------------------------------------
    // Wire DTOs (snake_case mapped via JsonOptions)
    // -------------------------------------------------------------------------

    private sealed record PaymentIntentResponse(
        string? Id,
        string? Status,
        string? ClientSecret,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record StatusResponse(
        string? Id,
        string? Status,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record ErrorResponse(
        string? ErrorCode,
        string? ErrorMessage);
}
