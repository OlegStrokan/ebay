using Protos.Product;

namespace ProductAdmin.Endpoints;

public static class CatalogItemEndpoints
{
    public static void MapCatalogItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/catalog-items").WithTags("Catalog Items");

        group.MapPost("/", async (CreateCatalogItemBody body, ProductService.ProductServiceClient client) =>
        {
            var request = new CreateCatalogItemRequest
            {
                Name = body.Name,
                Description = body.Description,
                CategoryId = body.CategoryId,
                Gtin = body.Gtin ?? string.Empty,
            };
            request.Attributes.AddRange(body.Attributes.Select(a => new ListingAttributeProto { Key = a.Key, Value = a.Value }));
            request.ImageUrls.AddRange(body.ImageUrls);
            var response = await client.CreateCatalogItemAsync(request);
            return Results.Created($"/catalog-items/{response.CatalogItemId}", new { response.CatalogItemId });
        });

        group.MapPut("/{id}", async (string id, UpdateCatalogItemBody body, ProductService.ProductServiceClient client) =>
        {
            var request = new UpdateCatalogItemRequest
            {
                CatalogItemId = id,
                Name = body.Name,
                Description = body.Description,
                CategoryId = body.CategoryId,
                Gtin = body.Gtin ?? string.Empty,
            };
            request.Attributes.AddRange(body.Attributes.Select(a => new ListingAttributeProto { Key = a.Key, Value = a.Value }));
            request.ImageUrls.AddRange(body.ImageUrls);
            await client.UpdateCatalogItemAsync(request);
            return Results.NoContent();
        });

        group.MapPost("/with-listing", async (CreateCatalogItemWithListingBody body, ProductService.ProductServiceClient client) =>
        {
            var request = new CreateCatalogItemWithListingRequest
            {
                SellerId = body.SellerId,
                Name = body.Name,
                Description = body.Description,
                CategoryId = body.CategoryId,
                Currency = body.Currency,
                InitialStock = body.InitialStock,
                Gtin = body.Gtin ?? string.Empty,
                Condition = body.Condition ?? "New",
                SellerNotes = body.SellerNotes ?? string.Empty,
            };
            request.Attributes.AddRange(body.Attributes.Select(a => new ListingAttributeProto { Key = a.Key, Value = a.Value }));
            request.ImageUrls.AddRange(body.ImageUrls);
            var response = await client.CreateCatalogItemWithListingAsync(request);
            return Results.Created($"/listings/{response.ListingId}", new { response.ListingId });
        });
    }

    public record CreateCatalogItemBody(
        string Name,
        string Description,
        string CategoryId,
        string? Gtin,
        List<AttributeItem> Attributes,
        List<string> ImageUrls);

    public record UpdateCatalogItemBody(
        string Name,
        string Description,
        string CategoryId,
        string? Gtin,
        List<AttributeItem> Attributes,
        List<string> ImageUrls);

    public record CreateCatalogItemWithListingBody(
        string SellerId,
        string Name,
        string Description,
        string CategoryId,
        string Currency,
        int InitialStock,
        string? Gtin,
        string? Condition,
        string? SellerNotes,
        List<AttributeItem> Attributes,
        List<string> ImageUrls);

    public record AttributeItem(string Key, string Value);
}
