using Gateway.Api.Contracts.B2BOrders;
using Gateway.Api.Contracts.Common;
using Gateway.Api.Mappers;
using GrpcOrder = Protos.Order;

namespace Gateway.Api.Endpoints;

public static class B2BOrderEndpoints
{
    public static RouteGroupBuilder MapB2BOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/b2b-orders")
            .WithTags("B2B Orders")
            .RequireAuthorization();

        group.MapPost("/", async (StartB2BOrderRequest request, GrpcOrder.B2BOrderService.B2BOrderServiceClient client) =>
        {
            var response = await client.StartB2BOrderAsync(new GrpcOrder.StartB2BOrderRequest
            {
                CustomerId = request.CustomerId,
                CompanyName = request.CompanyName,
                DeliveryAddress = OrderEndpoints.MapAddressToProto(request.DeliveryAddress),
                IdempotencyKey = request.IdempotencyKey
            });

            return response.Success
                ? Results.Created($"/api/v1/b2b-orders/{response.B2BOrderId}",
                    new ApiResponse<B2BOrderActionResponse>(new B2BOrderActionResponse(true, response.B2BOrderId, null)))
                : Results.UnprocessableEntity(
                    new ApiResponse<B2BOrderActionResponse>(new B2BOrderActionResponse(false, response.B2BOrderId, response.ErrorMessage)));
        });

        group.MapGet("/{id}", async (string id, GrpcOrder.B2BOrderService.B2BOrderServiceClient client) =>
        {
            var response = await client.GetB2BOrderAsync(new GrpcOrder.GetB2BOrderRequest { B2BOrderId = id });
            return Results.Ok(new ApiResponse<B2BOrderDetailsResponse>(MapB2BOrderDetails(response.B2BOrder)));
        });

        group.MapPatch("/{id}/quote", async (string id, UpdateQuoteDraftRequest request, GrpcOrder.B2BOrderService.B2BOrderServiceClient client) =>
        {
            var grpcRequest = new GrpcOrder.UpdateQuoteDraftRequest
            {
                B2BOrderId = id,
                Comment = request.Comment,
                CommentAuthor = request.CommentAuthor
            };
            grpcRequest.Changes.AddRange(request.Changes.Select(c => new GrpcOrder.QuoteItemChange
            {
                ChangeType = c.ChangeType,
                ProductId = c.ProductId,
                Quantity = c.Quantity,
                Price = DecimalValueMapper.ToProto(c.Price),
                Currency = c.Currency
            }));

            var response = await client.UpdateQuoteDraftAsync(grpcRequest);

            return Results.Ok(new ApiResponse<B2BOrderActionResponse>(new B2BOrderActionResponse(response.Success, response.B2BOrderId, response.ErrorMessage)));
        });

        group.MapPost("/{id}/finalize", async (string id, FinalizeQuoteRequest request, GrpcOrder.B2BOrderService.B2BOrderServiceClient client) =>
        {
            var response = await client.FinalizeQuoteAsync(new GrpcOrder.FinalizeQuoteRequest
            {
                B2BOrderId = id,
                PaymentMethod = request.PaymentMethod,
                IdempotencyKey = request.IdempotencyKey
            });

            return response.Success
                ? Results.Ok(new ApiResponse<FinalizeQuoteResponse>(new FinalizeQuoteResponse(true, response.B2BOrderId, response.OrderId, null)))
                : Results.UnprocessableEntity(
                    new ApiResponse<FinalizeQuoteResponse>(new FinalizeQuoteResponse(false, response.B2BOrderId, response.OrderId, response.ErrorMessage)));
        });

        group.MapPost("/{id}/cancel", async (string id, CancelB2BOrderRequest request, GrpcOrder.B2BOrderService.B2BOrderServiceClient client) =>
        {
            var grpcRequest = new GrpcOrder.CancelB2BOrderRequest { B2BOrderId = id };
            grpcRequest.Reasons.AddRange(request.Reasons);

            var response = await client.CancelB2BOrderAsync(grpcRequest);

            return Results.Ok(new ApiResponse<B2BOrderActionResponse>(new B2BOrderActionResponse(response.Success, response.B2BOrderId, response.ErrorMessage)));
        });

        return group;
    }

    private static B2BOrderDetailsResponse MapB2BOrderDetails(GrpcOrder.B2BOrderDetails o) => new(
        o.Id,
        o.CustomerId,
        o.CompanyName,
        o.Status,
        DecimalValueMapper.ToDecimal(o.TotalPrice),
        o.Currency,
        DecimalValueMapper.ToDecimal(o.DiscountPercent),
        OrderEndpoints.MapAddressFromProto(o.DeliveryAddress),
        o.Items.Select(i => new B2BLineItemResponse(
            i.LineItemId,
            i.ProductId,
            i.Quantity,
            DecimalValueMapper.ToDecimal(i.UnitPrice),
            DecimalValueMapper.ToDecimal(i.AdjustedUnitPrice),
            i.Currency,
            i.IsRemoved)).ToList(),
        o.Comments.ToList(),
        o.RequestedDeliveryDate,
        o.FinalizedOrderId,
        o.Version);
}
