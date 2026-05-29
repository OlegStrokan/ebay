using Application.DTOs;

namespace Application.Interfaces;

public interface IProductReadRepository
{
    Task<ProductDetailDto?> GetByIdAsync(Guid productId, CancellationToken ct = default);
    Task<List<ProductDetailDto>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<List<ProductPriceDto>> GetPricesByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<PagedResult<ProductDetailDto>> GetPendingReviewAsync(int page, int size, CancellationToken ct = default);
}
