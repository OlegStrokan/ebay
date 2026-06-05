using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Contracts.Shipping;
using Gateway.Api.Services;

namespace Gateway.Api.Endpoints;

public static class ShippingWebhookEndpoints
{
    public static RouteGroupBuilder MapShippingWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/webhooks/shipping")
            .WithTags("ShippingWebhooks");

        group.MapPost("/returns/delivered", async (
            ReturnShipmentDeliveredWebhookRequest request,
            HttpContext httpContext,
            IConfiguration configuration,
            IOrderSagaEventPublisher sagaPublisher,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(httpContext, configuration))
            {
                return Results.Unauthorized();
            }

            var orderIdCandidate = request.OrderId ?? httpContext.Request.Query["orderId"].ToString();

            if (!Guid.TryParse(orderIdCandidate, out var orderId))
            {
                return Results.BadRequest(new { error = "orderId is required and must be a valid GUID." });
            }

            if (string.IsNullOrWhiteSpace(request.ShipmentId))
            {
                return Results.BadRequest(new { error = "shipmentId is required." });
            }

            var deliveredAt = request.DeliveredAt ?? DateTime.UtcNow;

            await sagaPublisher.PublishReturnShipmentDeliveredAsync(
                orderId: orderId,
                shipmentId: request.ShipmentId,
                trackingNumber: request.TrackingNumber,
                deliveredAt: deliveredAt,
                ct);

            return Results.Accepted();
        });

        return group;
    }

    private static bool IsAuthorized(HttpContext httpContext, IConfiguration configuration)
    {
        var configuredSecret = configuration["WebhookSecurity:ShippingSharedSecret"];

        if (string.IsNullOrWhiteSpace(configuredSecret))
        {
            return true;
        }

        var providedSecret = httpContext.Request.Headers["X-Shipping-Webhook-Secret"].ToString();
        if (string.IsNullOrWhiteSpace(providedSecret))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(configuredSecret);
        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
