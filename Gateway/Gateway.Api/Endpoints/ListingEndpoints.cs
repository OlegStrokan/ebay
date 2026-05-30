using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Listings;
using Gateway.Api.Mappers;
using GrpcProduct = Protos.Product;

namespace Gateway.Api.Endpoints;

public static class ListingEndpoints
{
    public static RouteGroupBuilder MapListingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/listings").WithTags("Listings");

        group.MapGet("/catalog-item/{catalogItemId}", async (
            string catalogItemId,
            int? page,
            int? size,
            string? sortBy,
            string? condition,
            GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var response = await client.GetListingsForCatalogItemAsync(
                new GrpcProduct.GetListingsForCatalogItemRequest
                {
                    CatalogItemId = catalogItemId,
                    Page = page ?? 1,
                    Size = size ?? 20,
                    SortBy = sortBy ?? "price",
                    ConditionFilter = condition ?? string.Empty,
                });

            return Results.Ok(new ApiResponse<GetListingsForCatalogItemResponse>(new GetListingsForCatalogItemResponse(
                response.Listings.Select(MapListingDetail).ToList(),
                response.TotalCount)));
        })
        .WithName("GetListingsForCatalogItem");

        group.MapGet("/{id}", async (string id, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var response = await client.GetListingAsync(new GrpcProduct.GetListingRequest { ListingId = id });
            return Results.Ok(new ApiResponse<ListingDetailResponse>(MapListingDetail(response.Listing)));
        });

        group.MapGet("/seller/{sellerId}", async (
            string sellerId, int? page, int? size,
            GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var response = await client.GetSellerListingsAsync(
                new GrpcProduct.GetSellerListingsRequest { SellerId = sellerId, Page = page ?? 1, Size = size ?? 20 });
            return Results.Ok(new ApiResponse<GetListingsForCatalogItemResponse>(new GetListingsForCatalogItemResponse(
                response.Listings.Select(MapListingDetail).ToList(),
                response.TotalCount)));
        });

        group.MapPost("/", async (CreateListingRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var response = await client.CreateListingAsync(new GrpcProduct.CreateListingRequest
            {
                CatalogItemId = request.CatalogItemId,
                SellerId = request.SellerId,
                Price = DecimalValueMapper.ToProto(request.Price),
                Currency = request.Currency,
                InitialStock = request.InitialStock,
                Condition = request.Condition,
                SellerNotes = request.SellerNotes ?? string.Empty,
            });
            return Results.Created($"/api/v1/listings/{response.ListingId}",
                new ApiResponse<CreateListingResponse>(new CreateListingResponse(response.ListingId)));
        });

        group.MapPut("/{id}", async (string id, UpdateCatalogItemAndListingRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var grpcRequest = new GrpcProduct.UpdateCatalogItemAndListingRequest
            {
                ListingId = id,
                Name = request.Name,
                Description = request.Description,
                CategoryId = request.CategoryId,
                Price = DecimalValueMapper.ToProto(request.Price),
                Currency = request.Currency,
                Gtin = request.Gtin ?? string.Empty,
                Condition = request.Condition ?? string.Empty,
                SellerNotes = request.SellerNotes ?? string.Empty,
            };
            grpcRequest.Attributes.AddRange(
                request.Attributes.Select(a => new GrpcProduct.ProductAttributeProto { Key = a.Key, Value = a.Value }));
            grpcRequest.ImageUrls.AddRange(request.ImageUrls);
            await client.UpdateCatalogItemAndListingAsync(grpcRequest);
            return Results.NoContent();
        });

        group.MapPost("/{id}/activate", async (string id, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            await client.ActivateListingAsync(new GrpcProduct.ActivateListingRequest { ListingId = id });
            return Results.NoContent();
        });

        group.MapPost("/{id}/deactivate", async (string id, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            await client.DeactivateListingAsync(new GrpcProduct.DeactivateListingRequest { ListingId = id });
            return Results.NoContent();
        });

        group.MapDelete("/{id}", async (string id, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            await client.DeleteListingAsync(new GrpcProduct.DeleteListingRequest { ListingId = id });
            return Results.NoContent();
        });

        group.MapPut("/{id}/stock", async (string id, UpdateListingStockRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            await client.UpdateListingStockAsync(new GrpcProduct.UpdateListingStockRequest { ListingId = id, NewQuantity = request.NewQuantity });
            return Results.NoContent();
        });

        group.MapPut("/{id}/price", async (string id, ChangeListingPriceRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            await client.ChangeListingPriceAsync(new GrpcProduct.ChangeListingPriceRequest
            {
                ListingId = id,
                Price = DecimalValueMapper.ToProto(request.Price),
                Currency = request.Currency
            });
            return Results.NoContent();
        });

        return group;
    }

    private static ListingDetailResponse MapListingDetail(GrpcProduct.ListingDetail l) => new(
        l.ListingId,
        l.Name,
        l.Description,
        l.CategoryId,
        l.CategoryName,
        DecimalValueMapper.ToDecimal(l.Price),
        l.Currency,
        l.Stock,
        l.Attributes.Select(a => new ListingAttributeResponse(a.Key, a.Value)).ToList(),
        l.ImageUrls.ToList(),
        l.CatalogItemId,
        l.SellerId,
        l.Status,
        l.Condition,
        string.IsNullOrEmpty(l.Gtin) ? null : l.Gtin,
        string.IsNullOrEmpty(l.SellerNotes) ? null : l.SellerNotes);
}
