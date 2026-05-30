using Protos.Product;

namespace ProductAdmin.Endpoints;

public static class ProductAdminEndpoints
{
    public static void MapProductAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/products").WithTags("Product Admin");

        group.MapPut("/{id}", async (string id, UpdateProductBody body, ProductService.ProductServiceClient client) =>
        {
            var request = new UpdateProductRequest
            {
                ProductId = id,
                Name = body.Name,
                Description = body.Description,
                CategoryId = body.CategoryId,
                Currency = body.Currency,
            };
            request.Attributes.AddRange(body.Attributes.Select(a => new ProductAttributeProto { Key = a.Key, Value = a.Value }));
            request.ImageUrls.AddRange(body.ImageUrls);
            await client.UpdateProductAsync(request);
            return Results.NoContent();
        });

        group.MapDelete("/{id}", async (string id, ProductService.ProductServiceClient client) =>
        {
            await client.DeleteProductAsync(new DeleteProductRequest { ProductId = id });
            return Results.NoContent();
        });

        group.MapPost("/{id}/activate", async (string id, ProductService.ProductServiceClient client) =>
        {
            await client.ActivateProductAsync(new ActivateProductRequest { ProductId = id });
            return Results.NoContent();
        });

        group.MapPost("/{id}/deactivate", async (string id, ProductService.ProductServiceClient client) =>
        {
            await client.DeactivateProductAsync(new DeactivateProductRequest { ProductId = id });
            return Results.NoContent();
        });

        group.MapPut("/{id}/stock", async (string id, UpdateStockBody body, ProductService.ProductServiceClient client) =>
        {
            await client.UpdateProductStockAsync(new UpdateProductStockRequest { ProductId = id, NewQuantity = body.NewQuantity });
            return Results.NoContent();
        });
    }

    public record UpdateProductBody(
        string Name,
        string Description,
        string CategoryId,
        string Currency,
        List<AttributeItem> Attributes,
        List<string> ImageUrls);

    public record AttributeItem(string Key, string Value);
    public record UpdateStockBody(int NewQuantity);
}
