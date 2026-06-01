using System.Security.Claims;
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

        group.MapPost("/", async (CreateProductRequest request, ClaimsPrincipal user, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var tokenSellerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(tokenSellerId) || tokenSellerId != request.SellerId)
                return Results.Forbid();

            var grpcRequest = new GrpcProduct.CreateProductRequest
            {
                SellerId = request.SellerId,
                Name = request.Name,
                Description = request.Description,
                CategoryId = request.CategoryId,
                Price = DecimalValueMapper.ToProto(request.Price),
                Currency = request.Currency,
                InitialStock = request.InitialStock,
            };
            grpcRequest.Attributes.AddRange(
                request.Attributes.Select(a => new GrpcProduct.ProductAttributeProto { Key = a.Key, Value = a.Value }));
            grpcRequest.ImageUrls.AddRange(request.ImageUrls);

            var response = await client.CreateProductAsync(grpcRequest);
            return Results.Created($"/api/v1/products/{response.ProductId}",
                new ApiResponse<CreateProductResponse>(new CreateProductResponse(response.ProductId, response.Status)));
        }).RequireAuthorization();

        group.MapPost("/{id}/status", async (string id, GetProductStatusRequest request, GrpcProduct.ProductService.ProductServiceClient client) =>
        {
            var response = await client.GetProductStatusAsync(
                new GrpcProduct.GetProductStatusRequest { ProductId = id, SellerId = request.SellerId });
            return Results.Ok(new ApiResponse<ProductStatusResponse>(
                new ProductStatusResponse(response.Status, string.IsNullOrEmpty(response.ReviewNotes) ? null : response.ReviewNotes)));
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
        p.ImageUrls.ToList(),
        string.IsNullOrEmpty(p.SellerId) ? null : p.SellerId,
        string.IsNullOrEmpty(p.Status) ? null : p.Status,
        string.IsNullOrEmpty(p.ReviewNotes) ? null : p.ReviewNotes);
}
