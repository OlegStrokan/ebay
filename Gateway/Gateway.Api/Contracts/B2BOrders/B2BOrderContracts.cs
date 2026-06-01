using Gateway.Api.Contracts.Common;

namespace Gateway.Api.Contracts.B2BOrders;

public sealed record StartB2BOrderRequest(
    string CustomerId,
    string CompanyName,
    AddressDto DeliveryAddress,
    string IdempotencyKey);

public sealed record B2BOrderActionResponse(bool Success, string B2BOrderId, string? ErrorMessage);

public sealed record UpdateQuoteDraftRequest(
    IReadOnlyList<QuoteItemChangeDto> Changes,
    string Comment);

public sealed record QuoteItemChangeDto(
    string ChangeType,
    string ProductId,
    int Quantity,
    decimal Price,
    string Currency);

public sealed record FinalizeQuoteRequest(string PaymentMethod, string IdempotencyKey);
public sealed record FinalizeQuoteResponse(bool Success, string B2BOrderId, string OrderId, string? ErrorMessage);

public sealed record CancelB2BOrderRequest(IReadOnlyList<string> Reasons);

public sealed record B2BOrderDetailsResponse(
    string Id,
    string CustomerId,
    string CompanyName,
    string? CompanyId,
    string Status,
    decimal TotalPrice,
    string Currency,
    decimal DiscountPercent,
    AddressDto DeliveryAddress,
    IReadOnlyList<B2BLineItemResponse> Items,
    IReadOnlyList<string> Comments,
    string RequestedDeliveryDate,
    string FinalizedOrderId,
    int Version);

public sealed record B2BLineItemResponse(
    string LineItemId,
    string ProductId,
    int Quantity,
    decimal UnitPrice,
    decimal AdjustedUnitPrice,
    string Currency,
    bool IsRemoved);
