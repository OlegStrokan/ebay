namespace Domain.Entities.B2BOrder;

public record B2BOrderSnapshotState(
    Guid Id,
    Guid CustomerId,
    string CompanyName,
    string Status,
    decimal DiscountPercent,
    string Street,
    string City,
    string Country,
    string PostalCode,
    DateTime? RequestedDeliveryDate,
    Guid? FinalizedOrderId,
    int Version,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<QuoteLineItemSnapshotState> Items,
    List<string> Comments,
    Guid? CompanyId = null);
