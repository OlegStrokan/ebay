using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.RecurringOrders;
using Gateway.Api.Mappers;
using GrpcOrder = Protos.Order;

namespace Gateway.Api.Endpoints;

public static class RecurringOrderEndpoints
{
    public static RouteGroupBuilder MapRecurringOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/recurring-orders")
            .WithTags("Recurring Orders")
            .RequireAuthorization();

        group.MapPost("/", async (CreateRecurringOrderRequest request, GrpcOrder.RecurringOrderService.RecurringOrderServiceClient client) =>
        {
            var grpcRequest = new GrpcOrder.CreateRecurringOrderRequest
            {
                CustomerId = request.CustomerId,
                PaymentMethod = request.PaymentMethod,
                Frequency = request.Frequency,
                DeliveryAddress = OrderEndpoints.MapAddressToProto(request.DeliveryAddress),
                FirstRunAt = request.FirstRunAt ?? "",
                MaxExecutions = request.MaxExecutions,
                IdempotencyKey = request.IdempotencyKey
            };
            grpcRequest.Items.AddRange(request.Items.Select(i => new GrpcOrder.RecurringItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                Price = DecimalValueMapper.ToProto(i.Price),
                Currency = i.Currency
            }));

            var response = await client.CreateRecurringOrderAsync(grpcRequest);

            return response.Success
                ? Results.Created($"/api/v1/recurring-orders/{response.RecurringOrderId}",
                    new ApiResponse<RecurringOrderActionResponse>(new RecurringOrderActionResponse(true, response.RecurringOrderId, null)))
                : Results.UnprocessableEntity(
                    new ApiResponse<RecurringOrderActionResponse>(new RecurringOrderActionResponse(false, response.RecurringOrderId, response.ErrorMessage)));
        });

        group.MapGet("/{id}", async (string id, GrpcOrder.RecurringOrderService.RecurringOrderServiceClient client) =>
        {
            var response = await client.GetRecurringOrderAsync(
                new GrpcOrder.GetRecurringOrderRequest { RecurringOrderId = id });

            return Results.Ok(new ApiResponse<RecurringOrderDetailsResponse>(MapRecurringOrderDetails(response.Order)));
        });

        group.MapGet("/customer/{customerId}", async (string customerId, GrpcOrder.RecurringOrderService.RecurringOrderServiceClient client) =>
        {
            var response = await client.GetCustomerRecurringOrdersAsync(
                new GrpcOrder.GetCustomerRecurringOrdersRequest { CustomerId = customerId });

            return Results.Ok(new ApiResponse<IReadOnlyList<RecurringOrderSummaryResponse>>(response.Orders.Select(MapRecurringOrderSummary).ToList()));
        });

        group.MapPost("/{id}/pause", async (string id, GrpcOrder.RecurringOrderService.RecurringOrderServiceClient client) =>
        {
            var response = await client.PauseRecurringOrderAsync(
                new GrpcOrder.PauseRecurringOrderRequest { RecurringOrderId = id });

            return Results.Ok(new ApiResponse<RecurringOrderActionResponse>(new RecurringOrderActionResponse(response.Success, response.RecurringOrderId, response.ErrorMessage)));
        });

        group.MapPost("/{id}/resume", async (string id, GrpcOrder.RecurringOrderService.RecurringOrderServiceClient client) =>
        {
            var response = await client.ResumeRecurringOrderAsync(
                new GrpcOrder.ResumeRecurringOrderRequest { RecurringOrderId = id });

            return Results.Ok(new ApiResponse<RecurringOrderActionResponse>(new RecurringOrderActionResponse(response.Success, response.RecurringOrderId, response.ErrorMessage)));
        });

        group.MapPost("/{id}/cancel", async (string id, CancelRecurringOrderRequest request, GrpcOrder.RecurringOrderService.RecurringOrderServiceClient client) =>
        {
            var response = await client.CancelRecurringOrderAsync(
                new GrpcOrder.CancelRecurringOrderRequest
                {
                    RecurringOrderId = id,
                    Reason = request.Reason
                });

            return Results.Ok(new ApiResponse<RecurringOrderActionResponse>(new RecurringOrderActionResponse(response.Success, response.RecurringOrderId, response.ErrorMessage)));
        });

        return group;
    }

    private static RecurringOrderDetailsResponse MapRecurringOrderDetails(GrpcOrder.RecurringOrderDetails o) => new(
        o.Id,
        o.CustomerId,
        o.PaymentMethod,
        o.Frequency,
        o.Status,
        o.NextRunAt,
        o.LastRunAt,
        o.TotalExecutions,
        o.MaxExecutions,
        OrderEndpoints.MapAddressFromProto(o.DeliveryAddress),
        o.Items.Select(i => new RecurringOrderItemResponse(
            i.ProductId, i.Quantity, DecimalValueMapper.ToDecimal(i.Price), i.Currency)).ToList(),
        o.CreatedAt,
        o.UpdatedAt,
        o.Version);

    private static RecurringOrderSummaryResponse MapRecurringOrderSummary(GrpcOrder.RecurringOrderSummaryProto o) => new(
        o.Id, o.Frequency, o.Status, o.NextRunAt, o.TotalExecutions, o.CreatedAt);
}
