using System.Security.Claims;
using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Payments;
using Gateway.Api.Mappers;
using GrpcPayment = Protos.Payment;

namespace Gateway.Api.Endpoints;

public static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/payments")
            .WithTags("Payments")
            .RequireAuthorization();

        group.MapGet("/{id}", async (string id, HttpContext httpContext, GrpcPayment.PaymentService.PaymentServiceClient client) =>
        {
            var response = await client.GetPaymentAsync(new GrpcPayment.GetPaymentRequest { PaymentId = id });

            if (!response.Success)
                return Results.NotFound(response.ErrorMessage);

            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (response.Payment.CustomerId != userId)
                return Results.Forbid();

            return Results.Ok(new ApiResponse<PaymentDetailsResponse>(MapPaymentDetails(response.Payment)));
        });

        group.MapGet("/order/{orderId}", async (
            string orderId,
            string idempotencyKey,
            HttpContext httpContext,
            GrpcPayment.PaymentService.PaymentServiceClient client) =>
        {
            var response = await client.GetPaymentByOrderAndIdempotencyAsync(
                new GrpcPayment.GetPaymentByOrderAndIdempotencyRequest
                {
                    OrderId = orderId,
                    IdempotencyKey = idempotencyKey
                });

            if (!response.Success)
                return Results.NotFound(response.ErrorMessage);

            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (response.Payment.CustomerId != userId)
                return Results.Forbid();

            return Results.Ok(new ApiResponse<PaymentDetailsResponse>(MapPaymentDetails(response.Payment)));
        });

        return group;
    }

    private static PaymentDetailsResponse MapPaymentDetails(GrpcPayment.PaymentDetails p) => new(
        p.PaymentId,
        p.OrderId,
        p.CustomerId,
        DecimalValueMapper.ToDecimal(p.Amount),
        p.Currency,
        p.PaymentMethod,
        p.Status.ToString(),
        NullIfEmpty(p.ProviderPaymentIntentId),
        NullIfEmpty(p.ProviderRefundId),
        NullIfEmpty(p.FailureCode),
        NullIfEmpty(p.FailureMessage),
        p.CreatedAtUnix,
        p.UpdatedAtUnix,
        p.SucceededAtUnix,
        p.FailedAtUnix);

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
