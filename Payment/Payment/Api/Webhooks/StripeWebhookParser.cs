using Application.Commands.HandleStripeWebhook;
using System.Text.Json;

namespace Api.Webhooks;

internal static class StripeWebhookParser
{
    public static bool TryParse(
        string payloadJson,
        out ParsedStripeWebhook parsedWebhook,
        out string? error)
    {
        parsedWebhook = default!;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            var providerEventId = GetString(root, "id");
            var eventType = GetString(root, "type");

            if (string.IsNullOrWhiteSpace(providerEventId))
            {
                error = "Stripe event id is missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(eventType))
            {
                error = "Stripe event type is missing";
                return false;
            }

            var dataObject = GetDataObject(root);

            var outcome = MapOutcome(eventType, dataObject);
            var paymentId = ExtractPaymentId(dataObject);
            var providerPaymentIntentId = ExtractProviderPaymentIntentId(eventType, dataObject);
            var providerRefundId = ExtractProviderRefundId(eventType, dataObject);
            var (failureCode, failureMessage) = ExtractFailure(dataObject);

            parsedWebhook = new ParsedStripeWebhook(
                ProviderEventId: providerEventId,
                EventType: eventType,
                Outcome: outcome,
                PaymentId: paymentId,
                ProviderPaymentIntentId: providerPaymentIntentId,
                ProviderRefundId: providerRefundId,
                FailureCode: failureCode,
                FailureMessage: failureMessage);

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON payload: {ex.Message}";
            return false;
        }
    }

    private static JsonElement? GetDataObject(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!dataElement.TryGetProperty("object", out var objectElement)
            || objectElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return objectElement;
    }

    private static StripeWebhookOutcome MapOutcome(string eventType, JsonElement? dataObject)
    {
        return eventType switch
        {
            "payment_intent.succeeded" => StripeWebhookOutcome.PaymentSucceeded,
            "payment_intent.payment_failed" => StripeWebhookOutcome.PaymentFailed,
            "payment_intent.canceled" => StripeWebhookOutcome.PaymentFailed,
            "payment_intent.expired" => StripeWebhookOutcome.PaymentFailed,
            "refund.succeeded" => StripeWebhookOutcome.RefundSucceeded,
            "refund.failed" => StripeWebhookOutcome.RefundFailed,
            "charge.refunded" => StripeWebhookOutcome.RefundSucceeded,
            "charge.refund.updated" => MapChargeRefundUpdatedOutcome(dataObject),
            _ => StripeWebhookOutcome.Unknown,
        };
    }

    private static StripeWebhookOutcome MapChargeRefundUpdatedOutcome(JsonElement? dataObject)
    {
        var status = GetString(dataObject, "status");
        return status?.ToLowerInvariant() switch
        {
            "failed" => StripeWebhookOutcome.RefundFailed,
            "succeeded" => StripeWebhookOutcome.RefundSucceeded,
            _ => StripeWebhookOutcome.Unknown,
        };
    }

    private static string? ExtractPaymentId(JsonElement? dataObject)
    {
        if (dataObject is null)
        {
            return null;
        }

        if (dataObject.Value.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            return GetString(metadata, "payment_id")
                   ?? GetString(metadata, "paymentId")
                   ?? GetString(metadata, "payment-id");
        }

        return GetString(dataObject, "payment_id");
    }

    private static string? ExtractProviderPaymentIntentId(string eventType, JsonElement? dataObject)
    {
        if (dataObject is null)
        {
            return null;
        }

        if (eventType.StartsWith("payment_intent.", StringComparison.Ordinal)
            && dataObject.Value.TryGetProperty("id", out var paymentIntentId)
            && paymentIntentId.ValueKind == JsonValueKind.String)
        {
            return paymentIntentId.GetString();
        }

        return GetString(dataObject, "payment_intent");
    }

    private static string? ExtractProviderRefundId(string eventType, JsonElement? dataObject)
    {
        if (dataObject is null)
        {
            return null;
        }

        if (eventType.StartsWith("refund.", StringComparison.Ordinal))
        {
            return GetString(dataObject, "id");
        }

        var directRefundId = GetString(dataObject, "refund");
        if (!string.IsNullOrWhiteSpace(directRefundId))
        {
            return directRefundId;
        }

        if (!dataObject.Value.TryGetProperty("refunds", out var refunds)
            || refunds.ValueKind != JsonValueKind.Object
            || !refunds.TryGetProperty("data", out var refundData)
            || refundData.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = refundData.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object
            ? GetString(first, "id")
            : null;
    }

    private static (string? FailureCode, string? FailureMessage) ExtractFailure(JsonElement? dataObject)
    {
        if (dataObject is null)
        {
            return (null, null);
        }

        if (dataObject.Value.TryGetProperty("last_payment_error", out var paymentError)
            && paymentError.ValueKind == JsonValueKind.Object)
        {
            var nestedCode = GetString(paymentError, "code");
            var nestedMessage = GetString(paymentError, "message");
            if (!string.IsNullOrWhiteSpace(nestedCode) || !string.IsNullOrWhiteSpace(nestedMessage))
            {
                return (nestedCode, nestedMessage);
            }
        }

        var failureCode = GetString(dataObject, "failure_code");
        var failureMessage = GetString(dataObject, "failure_message")
                             ?? GetString(dataObject, "reason");

        return (failureCode, failureMessage);
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}