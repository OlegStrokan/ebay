using Application.Gateways;
using Application.Gateways.Models;
using Domain.Enums;
using Infrastructure.Options;
using Microsoft.Extensions.Options;
using Stripe;
using DomainPaymentMethod = Domain.Enums.PaymentMethod;

namespace Infrastructure.Gateways;

internal sealed class StripePaymentProvider(
    IOptions<StripeOptions> stripeOptions,
    ILogger<StripePaymentProvider> logger) : IStripePaymentProvider
{
    private readonly StripeOptions _stripeOptions = stripeOptions.Value;
    private readonly IStripeClient _stripeClient = new StripeClient(stripeOptions.Value.SecretKey);

    public async Task<ProcessPaymentProviderResult> ProcessPaymentAsync(
        ProcessPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasSecretKey())
        {
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ClientSecret: null,
                ErrorCode: "stripe_secret_not_configured",
                ErrorMessage: "Stripe secret key is not configured.");
        }

        try
        {
            var currency = NormalizeCurrency(request.Currency);
            var amountInMinorUnits = ConvertToMinorUnits(request.Amount, currency);
            var createOptions = BuildPaymentIntentCreateOptions(request, currency, amountInMinorUnits);
            var requestOptions = new RequestOptions
            {
                IdempotencyKey = request.IdempotencyKey,
            };

            var paymentIntentService = new PaymentIntentService(_stripeClient);
            var paymentIntent = await paymentIntentService.CreateAsync(createOptions, requestOptions, cancellationToken);

            return MapProcessPaymentResult(paymentIntent);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe process payment call failed. PaymentId={PaymentId}", request.PaymentId);
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: ex.StripeError?.PaymentIntent?.Id,
                ClientSecret: ex.StripeError?.PaymentIntent?.ClientSecret,
                ErrorCode: ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage: ex.StripeError?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Stripe process payment error. PaymentId={PaymentId}", request.PaymentId);
            return new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ClientSecret: null,
                ErrorCode: "unexpected_provider_error",
                ErrorMessage: "Unexpected error while creating Stripe payment intent.");
        }
    }

    public async Task<RefundPaymentProviderResult> RefundPaymentAsync(
        RefundPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasSecretKey())
        {
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: null,
                ErrorCode: "stripe_secret_not_configured",
                ErrorMessage: "Stripe secret key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: null,
                ErrorCode: "missing_provider_payment_intent_id",
                ErrorMessage: "Provider payment intent id is required for refunds.");
        }

        try
        {
            var currency = NormalizeCurrency(request.Currency);
            var amountInMinorUnits = ConvertToMinorUnits(request.Amount, currency);

            var createOptions = new RefundCreateOptions
            {
                PaymentIntent = request.ProviderPaymentIntentId,
                Amount = amountInMinorUnits,
                Reason = "requested_by_customer",
                Metadata = new Dictionary<string, string>
                {
                    ["payment_id"] = request.PaymentId,
                    ["reason_text"] = request.Reason,
                },
            };

            var requestOptions = new RequestOptions
            {
                IdempotencyKey = request.IdempotencyKey,
            };

            var refundService = new RefundService(_stripeClient);
            var refund = await refundService.CreateAsync(createOptions, requestOptions, cancellationToken);

            return MapRefundPaymentResult(refund);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe refund call failed. PaymentId={PaymentId}", request.PaymentId);
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: null,
                ErrorCode: ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage: ex.StripeError?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Stripe refund error. PaymentId={PaymentId}", request.PaymentId);
            return new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: null,
                ErrorCode: "unexpected_provider_error",
                ErrorMessage: "Unexpected error while creating Stripe refund.");
        }
    }

    public async Task<ProviderPaymentStatusResult> GetPaymentStatusAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Unknown,
                ErrorCode: "missing_provider_payment_intent_id",
                ErrorMessage: "Provider payment intent id is required.");
        }

        if (!HasSecretKey())
        {
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Unknown,
                ErrorCode: "stripe_secret_not_configured",
                ErrorMessage: "Stripe secret key is not configured.");
        }

        try
        {
            var paymentIntentService = new PaymentIntentService(_stripeClient);
            var paymentIntent = await paymentIntentService.GetAsync(providerPaymentIntentId, null, null, cancellationToken);

            return MapPaymentStatusResult(paymentIntent);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe get payment status failed. ProviderPaymentIntentId={ProviderPaymentIntentId}", providerPaymentIntentId);
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Unknown,
                ErrorCode: ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage: ex.StripeError?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Stripe get payment status error. ProviderPaymentIntentId={ProviderPaymentIntentId}", providerPaymentIntentId);
            return new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Unknown,
                ErrorCode: "unexpected_provider_error",
                ErrorMessage: "Unexpected error while fetching Stripe payment status.");
        }
    }

    public async Task<ProviderRefundStatusResult> GetRefundStatusAsync(
        string providerRefundId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerRefundId))
        {
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Unknown,
                ErrorCode: "missing_provider_refund_id",
                ErrorMessage: "Provider refund id is required.");
        }

        if (!HasSecretKey())
        {
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Unknown,
                ErrorCode: "stripe_secret_not_configured",
                ErrorMessage: "Stripe secret key is not configured.");
        }

        try
        {
            var refundService = new RefundService(_stripeClient);
            var refund = await refundService.GetAsync(providerRefundId, null, null, cancellationToken);

            return MapRefundStatusResult(refund);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe get refund status failed. ProviderRefundId={ProviderRefundId}", providerRefundId);
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Unknown,
                ErrorCode: ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage: ex.StripeError?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Stripe get refund status error. ProviderRefundId={ProviderRefundId}", providerRefundId);
            return new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Unknown,
                ErrorCode: "unexpected_provider_error",
                ErrorMessage: "Unexpected error while fetching Stripe refund status.");
        }
    }

    public async Task<CapturePaymentProviderResult> CapturePaymentAsync(
        CapturePaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasSecretKey())
        {
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "stripe_secret_not_configured",
                ErrorMessage: "Stripe secret key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "missing_provider_payment_intent_id",
                ErrorMessage: "ProviderPaymentIntentId is required for capture.");
        }

        try
        {
            var requestOptions = new RequestOptions
            {
                IdempotencyKey = request.IdempotencyKey,
            };

            var paymentIntentService = new PaymentIntentService(_stripeClient);
            var paymentIntent = await paymentIntentService.CaptureAsync(
                request.ProviderPaymentIntentId,
                options: null,
                requestOptions,
                cancellationToken);

            var status = paymentIntent.Status?.Trim().ToLowerInvariant() == "succeeded"
                ? ProviderProcessPaymentStatus.Succeeded
                : ProviderProcessPaymentStatus.Failed;

            var errorCode = status == ProviderProcessPaymentStatus.Failed
                ? (paymentIntent.LastPaymentError?.Code ?? "capture_failed")
                : null;
            var errorMessage = status == ProviderProcessPaymentStatus.Failed
                ? (paymentIntent.LastPaymentError?.Message ?? "Stripe capture returned non-succeeded status.")
                : null;

            return new CapturePaymentProviderResult(
                Status: status,
                ProviderPaymentIntentId: paymentIntent.Id,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe capture call failed. ProviderPaymentIntentId={Id}", request.ProviderPaymentIntentId);
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: ex.StripeError?.PaymentIntent?.Id,
                ErrorCode: ex.StripeError?.Code ?? "stripe_error",
                ErrorMessage: ex.StripeError?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Stripe capture error. ProviderPaymentIntentId={Id}", request.ProviderPaymentIntentId);
            return new CapturePaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: null,
                ErrorCode: "unexpected_provider_error",
                ErrorMessage: "Unexpected error while capturing Stripe payment intent.");
        }
    }

    public async Task CancelAuthorizationAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
    {
        if (!HasSecretKey())
        {
            throw new InvalidOperationException("Stripe secret key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
        {
            throw new ArgumentException("ProviderPaymentIntentId is required for cancel.", nameof(providerPaymentIntentId));
        }

        try
        {
            var paymentIntentService = new PaymentIntentService(_stripeClient);
            await paymentIntentService.CancelAsync(
                providerPaymentIntentId,
                options: null,
                requestOptions: null,
                cancellationToken);

            logger.LogInformation(
                "Successfully cancelled Stripe authorization for {ProviderPaymentIntentId}",
                providerPaymentIntentId);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe cancel authorization failed. ProviderPaymentIntentId={Id}", providerPaymentIntentId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error cancelling Stripe authorization. ProviderPaymentIntentId={Id}", providerPaymentIntentId);
            throw;
        }
    }

    // @todo: mappers should be moved to new files
    private static ProcessPaymentProviderResult MapProcessPaymentResult(PaymentIntent paymentIntent)
    {
        var status = paymentIntent.Status?.Trim().ToLowerInvariant();

        return status switch
        {
            "succeeded" => new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Succeeded,
                ProviderPaymentIntentId: paymentIntent.Id,
                ClientSecret: paymentIntent.ClientSecret,
                ErrorCode: null,
                ErrorMessage: null),

            "requires_action" => new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.RequiresAction,
                ProviderPaymentIntentId: paymentIntent.Id,
                ClientSecret: paymentIntent.ClientSecret,
                ErrorCode: null,
                ErrorMessage: null),

            "canceled" => new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Failed,
                ProviderPaymentIntentId: paymentIntent.Id,
                ClientSecret: paymentIntent.ClientSecret,
                ErrorCode: paymentIntent.CancellationReason ?? "payment_intent_canceled",
                ErrorMessage: paymentIntent.LastPaymentError?.Message ?? "Stripe payment intent was canceled."),

            _ => new ProcessPaymentProviderResult(
                Status: ProviderProcessPaymentStatus.Pending,
                ProviderPaymentIntentId: paymentIntent.Id,
                ClientSecret: paymentIntent.ClientSecret,
                ErrorCode: null,
                ErrorMessage: null),
        };
    }

    private static RefundPaymentProviderResult MapRefundPaymentResult(Refund refund)
    {
        var status = refund.Status?.Trim().ToLowerInvariant();

        return status switch
        {
            "succeeded" => new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Succeeded,
                ProviderRefundId: refund.Id,
                ErrorCode: null,
                ErrorMessage: null),

            "failed" or "canceled" => new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Failed,
                ProviderRefundId: refund.Id,
                ErrorCode: refund.FailureReason ?? "refund_failed",
                ErrorMessage: "Stripe refund failed."),

            _ => new RefundPaymentProviderResult(
                Status: ProviderRefundPaymentStatus.Pending,
                ProviderRefundId: refund.Id,
                ErrorCode: null,
                ErrorMessage: null),
        };
    }

    private static ProviderPaymentStatusResult MapPaymentStatusResult(PaymentIntent paymentIntent)
    {
        var status = paymentIntent.Status?.Trim().ToLowerInvariant();

        return status switch
        {
            "succeeded" => new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Succeeded,
                ErrorCode: null,
                ErrorMessage: null),

            "canceled" or "requires_payment_method" => new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Failed,
                ErrorCode: paymentIntent.CancellationReason ?? paymentIntent.LastPaymentError?.Code,
                ErrorMessage: paymentIntent.LastPaymentError?.Message),

            "requires_action" or "requires_confirmation" or "requires_capture" or "processing" =>
                new ProviderPaymentStatusResult(
                    Status: ProviderPaymentLifecycleStatus.Pending,
                    ErrorCode: null,
                    ErrorMessage: null),

            _ => new ProviderPaymentStatusResult(
                Status: ProviderPaymentLifecycleStatus.Unknown,
                ErrorCode: null,
                ErrorMessage: null),
        };
    }

    private static ProviderRefundStatusResult MapRefundStatusResult(Refund refund)
    {
        var status = refund.Status?.Trim().ToLowerInvariant();

        return status switch
        {
            "succeeded" => new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Succeeded,
                ErrorCode: null,
                ErrorMessage: null),

            "failed" or "canceled" => new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Failed,
                ErrorCode: refund.FailureReason ?? "refund_failed",
                ErrorMessage: "Stripe refund failed."),

            "pending" or "requires_action" => new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Pending,
                ErrorCode: null,
                ErrorMessage: null),

            _ => new ProviderRefundStatusResult(
                Status: ProviderRefundLifecycleStatus.Unknown,
                ErrorCode: null,
                ErrorMessage: null),
        };
    }

    private PaymentIntentCreateOptions BuildPaymentIntentCreateOptions(
        ProcessPaymentProviderRequest request,
        string currency,
        long amountInMinorUnits)
    {
        var options = new PaymentIntentCreateOptions
        {
            Amount = amountInMinorUnits,
            Currency = currency,
            Confirm = false,
            ReceiptEmail = request.CustomerEmail,
            Description = $"Order {request.OrderId}",
            Metadata = new Dictionary<string, string>
            {
                ["payment_id"] = request.PaymentId,
                ["order_id"] = request.OrderId,
                ["customer_id"] = request.CustomerId,
                ["idempotency_key"] = request.IdempotencyKey,
            },
        };

        var mappedPaymentMethodType = MapPaymentMethodType(request.PaymentMethod);
        if (mappedPaymentMethodType is null)
        {
            options.AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true,
            };
        }
        else
        {
            options.PaymentMethodTypes = [mappedPaymentMethodType];
        }

        return options;
    }

    private static string? MapPaymentMethodType(DomainPaymentMethod paymentMethod)
    {
        return paymentMethod switch
        {
            DomainPaymentMethod.Card => "card",
            DomainPaymentMethod.BankTransfer => "us_bank_account",
            DomainPaymentMethod.Wallet => "card",
            _ => null,
        };
    }

    private bool HasSecretKey() => !string.IsNullOrWhiteSpace(_stripeOptions.SecretKey);

    private string NormalizeCurrency(string? currency)
    {
        var value = string.IsNullOrWhiteSpace(currency)
            ? _stripeOptions.DefaultCurrency
            : currency;

        return value.Trim().ToLowerInvariant();
    }

    private static long ConvertToMinorUnits(decimal amount, string currency)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        }

        var normalizedCurrency = currency.Trim().ToLowerInvariant();
        var multiplier = IsZeroDecimalCurrency(normalizedCurrency) ? 1m : 100m;

        var rounded = decimal.Round(amount * multiplier, 0, MidpointRounding.AwayFromZero);
        return (long)rounded;
    }

    private static bool IsZeroDecimalCurrency(string currency)
    {
        return currency is "bif" or "clp" or "djf" or "gnf" or "jpy" or "kmf" or "krw" or
               "mga" or "pyg" or "rwf" or "ugx" or "vnd" or "vuv" or "xaf" or "xof" or "xpf";
    }
}