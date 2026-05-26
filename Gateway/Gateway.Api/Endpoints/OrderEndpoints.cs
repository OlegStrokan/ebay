using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Orders;
using Gateway.Api.Mappers;
using GrpcOrder = Protos.Order;

namespace Gateway.Api.Endpoints;

public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireAuthorization();

        group.MapPost("/", async (CreateOrderRequest request, GrpcOrder.OrderService.OrderServiceClient client) =>
        {
            var grpcRequest = new GrpcOrder.CreateOrderRequest
            {
                CustomerId = request.CustomerId,
                PaymentMethod = request.PaymentMethod,
                IdempotencyKey = request.IdempotencyKey,
                DeliveryAddress = MapAddressToProto(request.DeliveryAddress)
            };
            grpcRequest.Items.AddRange(request.Items.Select(MapOrderItemToProto));

            var response = await client.CreateOrderAsync(grpcRequest);

            return response.Success
                ? Results.Created($"/api/v1/orders/{response.OrderId}",
                    new ApiResponse<CreateOrderResponse>(new CreateOrderResponse(true, response.OrderId, null)))
                : Results.UnprocessableEntity(
                    new ApiResponse<CreateOrderResponse>(new CreateOrderResponse(false, response.OrderId, response.ErrorMessage)));
        });

        group.MapGet("/{id}", async (string id, GrpcOrder.OrderService.OrderServiceClient client) =>
        {
            var response = await client.GetOrderAsync(new GrpcOrder.GetOrderRequest { OrderId = id });
            return Results.Ok(new ApiResponse<OrderDetailsResponse>(MapOrderDetails(response.Order)));
        });

        group.MapGet("/", async (int? pageNumber, int? pageSize, GrpcOrder.OrderService.OrderServiceClient client) =>
        {
            var response = await client.ListOrdersAsync(new GrpcOrder.ListOrdersRequest
            {
                PageNumber = pageNumber ?? 1,
                PageSize = pageSize ?? 20
            });

            return Results.Ok(new ApiResponse<IReadOnlyList<OrderSummaryResponse>>(response.Orders.Select(MapOrderSummary).ToList()));
        });

        group.MapGet("/customer/{customerId}", async (string customerId, GrpcOrder.OrderService.OrderServiceClient client) =>
        {
            var response = await client.GetCustomerOrdersAsync(
                new GrpcOrder.GetCustomerOrdersRequest { CustomerId = customerId });

            return Results.Ok(new ApiResponse<IReadOnlyList<OrderSummaryResponse>>(response.Orders.Select(MapOrderSummary).ToList()));
        });

        group.MapPost("/{id}/return", async (string id, RequestReturnRequest request, GrpcOrder.OrderService.OrderServiceClient client) =>
        {
            var grpcRequest = new GrpcOrder.RequestReturnRequest
            {
                OrderId = id,
                Reason = request.Reason,
                IdempotencyKey = request.IdempotencyKey
            };
            grpcRequest.ItemsToReturn.AddRange(request.ItemsToReturn.Select(MapOrderItemToProto));

            var response = await client.RequestReturnAsync(grpcRequest);

            return response.Success
                ? Results.Ok(new ApiResponse<RequestReturnResponse>(new RequestReturnResponse(true, response.ReturnRequestId, null)))
                : Results.UnprocessableEntity(
                    new ApiResponse<RequestReturnResponse>(new RequestReturnResponse(false, response.ReturnRequestId, response.ErrorMessage)));
        });

        return group;
    }

    internal static GrpcOrder.Address MapAddressToProto(AddressDto a) => new()
    {
        Street = a.Street,
        City = a.City,
        Country = a.Country,
        PostalCode = a.PostalCode
    };

    internal static AddressDto MapAddressFromProto(GrpcOrder.Address a) => new(a.Street, a.City, a.Country, a.PostalCode);

    internal static GrpcOrder.OrderItem MapOrderItemToProto(OrderItemDto i) => new()
    {
        ProductId = i.ProductId,
        Quantity = i.Quantity,
        Price = DecimalValueMapper.ToProto(i.Price),
        Currency = i.Currency
    };

    private static OrderDetailsResponse MapOrderDetails(GrpcOrder.OrderDetails o) => new(
        o.Id,
        o.CustomerId,
        o.TrackingId,
        o.PaymentId,
        o.Status,
        DecimalValueMapper.ToDecimal(o.TotalAmount),
        o.Currency,
        MapAddressFromProto(o.DeliveryAddress),
        o.Items.Select(i => new OrderItemDetailResponse(
            i.ProductId, i.Quantity, DecimalValueMapper.ToDecimal(i.Price), i.Currency)).ToList(),
        o.CreatedAt,
        o.UpdatedAt,
        o.Version);

    private static OrderSummaryResponse MapOrderSummary(GrpcOrder.OrderSummary o) => new(
        o.Id, o.TrackingId, o.Status, DecimalValueMapper.ToDecimal(o.TotalAmount), o.Currency, o.CreatedAt);
}
