namespace Gateway.Api.Contracts.Products;

public sealed record GetProductsRequest(IReadOnlyList<string> ProductIds);
public sealed record GetProductPricesRequest(IReadOnlyList<string> ProductIds);

public sealed record ProductDetailResponse(
    string ProductId,
    string Name,
    string Description,
    string CategoryId,
    string CategoryName,
    decimal Price,
    string Currency,
    int Stock,
    IReadOnlyList<ProductAttributeResponse> Attributes,
    IReadOnlyList<string> ImageUrls,
    string? SellerId = null,
    string? Status = null,
    string? ReviewNotes = null);

public sealed record ProductAttributeResponse(string Key, string Value);

public sealed record ProductPriceResponse(string ProductId, decimal Price, string Currency);

public sealed record GetProductsResponse(
    IReadOnlyList<ProductDetailResponse> Products,
    IReadOnlyList<string> NotFoundIds);

public sealed record GetProductPricesResponse(
    IReadOnlyList<ProductPriceResponse> Prices,
    IReadOnlyList<string> NotFoundIds);

public sealed record CreateProductRequest(
    string SellerId,
    string Name,
    string Description,
    string CategoryId,
    decimal Price,
    string Currency,
    int InitialStock,
    List<ProductAttributeRequest> Attributes,
    List<string> ImageUrls);

public sealed record ProductAttributeRequest(string Key, string Value);

public sealed record CreateProductResponse(string ProductId, string Status);

public sealed record GetProductStatusRequest(string ProductId, string SellerId);
public sealed record ProductStatusResponse(string Status, string? ReviewNotes);
