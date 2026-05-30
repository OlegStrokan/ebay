namespace Gateway.Api.Contracts.Listings;

public sealed record ListingDetailResponse(
    string ListingId,
    string Name,
    string Description,
    string CategoryId,
    string CategoryName,
    decimal Price,
    string Currency,
    int Stock,
    IReadOnlyList<ListingAttributeResponse> Attributes,
    IReadOnlyList<string> ImageUrls,
    string CatalogItemId,
    string SellerId,
    string Status,
    string Condition,
    string? Gtin,
    string? SellerNotes);

public sealed record ListingAttributeResponse(string Key, string Value);

public sealed record GetListingsForCatalogItemResponse(
    IReadOnlyList<ListingDetailResponse> Listings,
    int TotalCount);

public sealed record CreateListingRequest(
    string CatalogItemId,
    string SellerId,
    decimal Price,
    string Currency,
    int InitialStock,
    string Condition,
    string? SellerNotes);

public sealed record CreateListingResponse(string ListingId);

public sealed record UpdateCatalogItemAndListingRequest(
    string Name,
    string Description,
    string CategoryId,
    decimal Price,
    string Currency,
    List<ListingAttributeRequest> Attributes,
    List<string> ImageUrls,
    string? Gtin,
    string? Condition,
    string? SellerNotes);

public sealed record ListingAttributeRequest(string Key, string Value);

public sealed record UpdateListingStockRequest(int NewQuantity);
public sealed record ChangeListingPriceRequest(decimal Price, string Currency);
