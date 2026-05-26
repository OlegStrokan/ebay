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
