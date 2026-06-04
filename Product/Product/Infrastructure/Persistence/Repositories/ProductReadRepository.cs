using Application.DTOs;
using Application.Interfaces;
using Domain.ValueObjects;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

internal sealed class ProductReadRepository(ProductDbContext dbContext) : IProductReadRepository
{
    public async Task<ProductDetailDto?> GetByIdAsync(Guid productId, CancellationToken ct = default)
    {
        var id      = ProductId.From(productId);
        var product = await dbContext.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return null;

        var categoryId = product.CategoryId.Value;
        var categoryNames = await GetCategoryNamesAsync([categoryId], ct);
        return MapToDetail(
            product,
            CategoryId.IsPlaceholder(product.CategoryId)
                ? string.Empty
                : categoryNames.GetValueOrDefault(categoryId, string.Empty));
    }

    public async Task<List<ProductDetailDto>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var productIds = ids.Select(ProductId.From).ToList();
        var products   = await dbContext.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(ct);

        var categoryIds = products
            .Select(p => p.CategoryId.Value)
            .Distinct()
            .ToList();

        var categoryNames = await GetCategoryNamesAsync(categoryIds, ct);

        return products.Select(p =>
            MapToDetail(
                p,
                CategoryId.IsPlaceholder(p.CategoryId)
                    ? string.Empty
                    : categoryNames.GetValueOrDefault(p.CategoryId.Value, string.Empty)))
            .ToList();
    }

    public async Task<List<ProductPriceDto>> GetPricesByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var productIds = ids.Select(ProductId.From).ToList();

        return await dbContext.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new ProductPriceDto(p.Id.Value, p.Price.Amount, p.Price.Currency, default, p.SellerId.Value))
            .ToListAsync(ct);
    }

    private Task<Dictionary<Guid, string>> GetCategoryNamesAsync(List<Guid> categoryIds, CancellationToken ct)
        => dbContext.Categories
            .AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id) && c.Id != CategoryId.PlaceholderGuid)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

    public async Task<PagedResult<ProductDetailDto>> GetPendingApprovalAsync(int page, int size, CancellationToken ct = default)
    {
        var query = dbContext.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.PendingApproval)
            .OrderBy(p => p.CreatedAt);

        var totalCount = await query.CountAsync(ct);

        var products = await query
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var categoryIds = products
            .Select(p => p.CategoryId.Value)
            .Distinct()
            .ToList();

        var categoryNames = await GetCategoryNamesAsync(categoryIds, ct);

        var items = products.Select(p =>
            MapToDetail(
                p,
                CategoryId.IsPlaceholder(p.CategoryId)
                    ? string.Empty
                    : categoryNames.GetValueOrDefault(p.CategoryId.Value, string.Empty))).ToList();

        return new PagedResult<ProductDetailDto>(items, totalCount, page, size);
    }

    private static ProductDetailDto MapToDetail(Domain.Entities.Product product, string categoryName)
        => new(
            product.Id.Value,
            product.Name,
            product.Description,
            CategoryId.IsPlaceholder(product.CategoryId) ? Guid.Empty : product.CategoryId.Value,
            categoryName,
            product.Price.Amount,
            product.Price.Currency,
            product.StockQuantity,
            product.Status.Name,
            product.SellerId.Value,
            product.Attributes.Select(a => new ProductAttributeDto(a.Key, a.Value)).ToList(),
            product.ImageUrls.ToList(),
            product.CreatedAt,
            product.UpdatedAt,
            Guid.Empty,
            null,
            "New",
            null,
            product.ReviewNotes);
}
