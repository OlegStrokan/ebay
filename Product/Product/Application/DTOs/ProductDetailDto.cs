namespace Application.DTOs;

public sealed record ProductDetailDto(
    Guid ProductId,
    string Name,
    string Description,
    Guid CategoryId,
    string CategoryName,
    decimal Price,
    string Currency,
    int StockQuantity,
    string Status,
    Guid SellerId,
    List<ProductAttributeDto> Attributes,
    List<string> ImageUrls,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    Guid CatalogItemId,
    string? Gtin,
    string Condition,
    string? SellerNotes,
    string? ReviewNotes = null);
