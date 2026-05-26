using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Inventory;
using GrpcInventory = Protos.Inventory;

namespace Gateway.Api.Endpoints;

public static class InventoryEndpoints
{
    public static RouteGroupBuilder MapInventoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/inventory")
            .WithTags("Inventory")
            .RequireAuthorization();

        group.MapPost("/reserve", async (ReserveInventoryRequest request, GrpcInventory.InventoryService.InventoryServiceClient client) =>
        {
            var grpcRequest = new GrpcInventory.ReserveInventoryRequest
            {
                OrderId = request.OrderId,
            };
            grpcRequest.Items.AddRange(request.Items.Select(i => new GrpcInventory.InventoryItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity
            }));

            var response = await client.ReserveInventoryAsync(grpcRequest);

            return response.Success
                ? Results.Ok(new ApiResponse<ReserveInventoryResponse>(new ReserveInventoryResponse(true, response.ReservationId, null)))
                : Results.UnprocessableEntity(
                    new ApiResponse<ReserveInventoryResponse>(new ReserveInventoryResponse(false, response.ReservationId, response.ErrorMessage)));
        });

        group.MapPost("/release", async (ReleaseInventoryRequest request, GrpcInventory.InventoryService.InventoryServiceClient client) =>
        {
            var response = await client.ReleaseInventoryAsync(
                new GrpcInventory.ReleaseInventoryRequest { ReservationId = request.ReservationId });

            return response.Success
                ? Results.Ok(new ApiResponse<ReleaseInventoryResponse>(new ReleaseInventoryResponse(true, null)))
                : Results.UnprocessableEntity(new ApiResponse<ReleaseInventoryResponse>(new ReleaseInventoryResponse(false, response.ErrorMessage)));
        });

        return group;
    }
}
