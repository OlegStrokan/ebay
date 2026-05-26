using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Products;
using Gateway.Api.Mappers;
using GrpcProduct = Protos.Product;

namespace Gateway.Api.Endpoints;

public static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/products").WithTags("Products");

        group.MapGet("/{id}", async (string id, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var response = await client.GetProductAsync(new GrpcProduct.GetProductRequest { ProductId = id });
            return Results.Ok(new ApiResponse<ProductDetailResponse>(MapProductDetail(response.Product)));
        });

        group.MapPost("/batch", async (GetProductsRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var grpcRequest = new GrpcProduct.GetProductsRequest();
            grpcRequest.ProductIds.AddRange(request.ProductIds);

            var response = await client.GetProductsAsync(grpcRequest);

            return Results.Ok(new ApiResponse<GetProductsResponse>(new GetProductsResponse(
                response.Products.Select(MapProductDetail).ToList(),
                response.NotFoundIds.ToList())));
        });

        group.MapPost("/prices", async (GetProductPricesRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var grpcRequest = new GrpcProduct.GetProductPricesRequest();
            grpcRequest.ProductIds.AddRange(request.ProductIds);

            var response = await client.GetProductPricesAsync(grpcRequest);

            return Results.Ok(new ApiResponse<GetProductPricesResponse>(new GetProductPricesResponse(
                response.Prices.Select(p => new ProductPriceResponse(
                    p.ProductId,
                    DecimalValueMapper.ToDecimal(p.Price),
                    p.Currency)).ToList(),
                response.NotFoundIds.ToList())));
        });

        return group;
    }

    private static ProductDetailResponse MapProductDetail(GrpcProduct.ProductDetail p) => new(
        p.ProductId,
        p.Name,
        p.Description,
        p.CategoryId,
        p.CategoryName,
        DecimalValueMapper.ToDecimal(p.Price),
        p.Currency,
        p.Stock,
        p.Attributes.Select(a => new ProductAttributeResponse(a.Key, a.Value)).ToList(),
        p.ImageUrls.ToList());
}
