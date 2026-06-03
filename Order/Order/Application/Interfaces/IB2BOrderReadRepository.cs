namespace Application.Interfaces;

public interface IB2BOrderReadRepository
{
    Task<B2BOrderDetail?> GetByIdAsync(Guid b2bOrderId, CancellationToken ct = default);
    Task<List<B2BOrderSummary>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
    Task<List<B2BOrderSummary>> GetByCompanyNameAsync(string companyName, CancellationToken ct = default);
    Task<List<B2BOrderSummary>> GetAllAsync(int pageNumber, int pageSize, CancellationToken ct = default);
}

public sealed record B2BOrderDetail(
    Guid Id,
    Guid CustomerId,
    string CompanyName,
    string Status,
    decimal TotalPrice,
    string Currency,
    decimal DiscountPercent,
    DateTime? RequestedDeliveryDate,
    Guid? FinalizedOrderId,
    AddressResponse DeliveryAddress,
    List<B2BLineItemResponse> Items,
    List<string> Comments,
    DateTime StartedAt,
    DateTime? UpdatedAt,
    int Version,
    Guid? CompanyId = null);

public sealed record B2BOrderSummary(
    Guid Id,
    string CompanyName,
    string Status,
    decimal TotalPrice,
    string Currency,
    DateTime StartedAt);

public sealed record B2BLineItemResponse(
    Guid LineItemId,
    Guid ProductId,
    int Quantity,
    decimal UnitPrice,
    decimal? AdjustedUnitPrice,
    string Currency,
    bool IsRemoved);
