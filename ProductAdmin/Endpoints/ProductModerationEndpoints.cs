using Protos.Product;

namespace ProductAdmin.Endpoints;

public static class ProductModerationEndpoints
{
    public static void MapProductModerationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/products").WithTags("Moderation");

        group.MapGet("/pending", async (int? page, int? size, ProductService.ProductServiceClient client) =>
        {
            var response = await client.GetPendingProductsAsync(
                new GetPendingProductsRequest { Page = page ?? 1, Size = size ?? 20 });
            return Results.Ok(response);
        });

        group.MapPost("/{id}/approve", async (string id, ProductService.ProductServiceClient client) =>
        {
            await client.ApproveProductAsync(new ApproveProductRequest { ProductId = id });
            return Results.NoContent();
        });

        group.MapPost("/{id}/reject", async (string id, RejectBody body, ProductService.ProductServiceClient client) =>
        {
            await client.RejectProductAsync(new RejectProductRequest { ProductId = id, Reason = body.Reason });
            return Results.NoContent();
        });
    }

    public record RejectBody(string Reason);
}
