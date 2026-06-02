using Infrastructure.Options;
using System.Security.Cryptography;
using System.Text;

namespace Api.Webhooks;

internal static class StripeWebhookSignatureVerifier
{
    public static bool TryValidate(
        string? signatureHeader,
        string payloadJson,
        StripeOptions options,
        out string? error)
    {
        error = null;

        // Bypass signature verification for the in-memory Fake provider (tests only).
        // MockFintech and real Stripe must always pass a valid signature.
        if (options.ProviderType == Infrastructure.Options.PaymentProviderType.Fake)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            error = "Stripe webhook secret is not configured";
            return false;
        }

        if (!TryParseSignatureHeader(signatureHeader, out var timestamp, out var signature))
        {
            error = "Stripe-Signature header is invalid";
            return false;
        }

        var tolerance = options.WebhookToleranceSeconds <= 0 ? 300 : options.WebhookToleranceSeconds;
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (Math.Abs(nowSeconds - timestamp) > tolerance)
        {
            error = "Stripe signature timestamp is outside allowed tolerance";
            return false;
        }

        var signedPayload = $"{timestamp}.{payloadJson}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.WebhookSecret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            error = "Stripe signature payload is not a valid hex string";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(computed, provided))
        {
            error = "Stripe webhook signature mismatch";
            return false;
        }

        return true;
    }

    private static bool TryParseSignatureHeader(
        string? signatureHeader,
        out long timestamp,
        out string signature)
    {
        timestamp = 0;
        signature = string.Empty;

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var parts = signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t")
            {
                long.TryParse(pair[1], out timestamp);
                continue;
            }

            if (pair[0] == "v1")
            {
                signature = pair[1];
            }
        }

        return timestamp > 0 && !string.IsNullOrWhiteSpace(signature);
    }
}