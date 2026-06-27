using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gateway.Api.Contracts.Shipping;
using Gateway.Api.Services;

namespace Gateway.Api.Endpoints;

public static class ShippingWebhookEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapShippingWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/webhooks/shipping")
            .WithTags("ShippingWebhooks");

        // carriers POST a tagged envelope { type, carrier, data } here. DPD signs the body
        // with HMAC-SHA256 (Stripe-Signature), PPL sends a plain X-PPL-Webhook-Secret header
        // only the terminal "return.delivered" event resumes the return saga; the progressive
        // in_transit / out_for_delivery events PPL emits are validated, logged, and discarded.
        // used in Return order saga
        group.MapPost("/returns/delivered", async (
            HttpContext httpContext,
            IConfiguration configuration,
            IOrderSagaEventPublisher sagaPublisher,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("ShippingWebhooks");

            // read the raw body up front: DPD's HMAC signature is computed over the exact bytes so jsonSerializer suck
            httpContext.Request.EnableBuffering();
            string rawBody;
            using (var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                rawBody = await reader.ReadToEndAsync(ct);
            }
            httpContext.Request.Body.Position = 0;

            ShippingWebhookEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ShippingWebhookEnvelope>(rawBody, JsonOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Malformed shipping webhook payload." });
            }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Type))
            {
                return Results.BadRequest(new { error = "carrier webhook 'type' is required." });
            }

            var carrier = (envelope.Carrier ?? string.Empty).Trim().ToLowerInvariant();

            var authorized = carrier switch
            {
                "dpd" => VerifyDpdHmac(
                    httpContext.Request.Headers["Stripe-Signature"],
                    rawBody,
                    configuration["WebhookSecurity:ShippingSharedSecret"]),
                "ppl" => VerifyPplSecret(
                    httpContext,
                    configuration["WebhookSecurity:PplSharedSecret"]),
                _ => false,
            };

            if (!authorized)
            {
                logger.LogWarning("Rejected shipping webhook from carrier '{Carrier}': authentication failed.", carrier);
                return Results.Unauthorized();
            }

            // Only the terminal delivered event resumes the saga. Intermediate progressive
            // events (parcel/return in_transit, out_for_delivery) are acknowledged and dropped.
            if (!string.Equals(envelope.Type, "return.delivered", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Discarding non-terminal shipping event '{Type}' from carrier '{Carrier}'.",
                    envelope.Type, carrier);
                return Results.Accepted();
            }

            // The saga always puts the correlating orderId on the callback query string;
            // fall back to the event body for robustness.
            var orderIdCandidate = httpContext.Request.Query["orderId"].ToString();
            if (string.IsNullOrWhiteSpace(orderIdCandidate))
            {
                orderIdCandidate = envelope.Data?.OrderId;
            }

            if (!Guid.TryParse(orderIdCandidate, out var orderId))
            {
                return Results.BadRequest(new { error = "orderId is required and must be a valid GUID." });
            }

            var shipmentId = envelope.Data?.ReturnShipmentId;
            if (string.IsNullOrWhiteSpace(shipmentId))
            {
                return Results.BadRequest(new { error = "returnShipmentId is required." });
            }

            await sagaPublisher.PublishReturnShipmentDeliveredAsync(
                orderId: orderId,
                shipmentId: shipmentId,
                trackingNumber: envelope.Data?.ReturnTrackingNumber,
                deliveredAt: DateTime.UtcNow,
                ct);

            logger.LogInformation(
                "Published ReturnShipmentDeliveredEvent for order {OrderId} (carrier '{Carrier}').",
                orderId, carrier);

            return Results.Accepted();
        });

        return group;
    }

    // DPD reuses the Stripe HMAC scheme: signature header "t={ts},v1={hex}", signed payload
    // "{ts}.{rawBody}", HMAC-SHA256 keyed by the shared secret
    private static bool VerifyDpdHmac(string? signatureHeader, string rawBody, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return true; // unconfigured (e.g. local dev) -> accept
        }
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        long timestamp = 0;
        var signature = string.Empty;
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t")
            {
                long.TryParse(pair[1], out timestamp);
            }
            else if (pair[0] == "v1")
            {
                signature = pair[1];
            }
        }

        if (timestamp <= 0 || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var signedPayload = $"{timestamp}.{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, provided);
    }

    // PPL does not sign the body; it sends the secret verbatim in a header.
    private static bool VerifyPplSecret(HttpContext httpContext, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return true; // unconfigured (e.g. local dev) -> accept
        }

        var provided = httpContext.Request.Headers["X-PPL-Webhook-Secret"].ToString();
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(provided));
    }
}
