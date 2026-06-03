using System.Text.Json;
using Application.Interfaces;
using Infrastructure.Persistence.DbContext;
using Infrastructure.ReadModels;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public class B2BOrderReadRepository(ReadDbContext dbContext) : IB2BOrderReadRepository
{
    public async Task<B2BOrderDetail?> GetByIdAsync(Guid b2bOrderId, CancellationToken ct = default)
    {
        var readModel = await dbContext.B2BOrderReadModels
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == b2bOrderId, ct);

        return readModel is null ? null : MapToDetail(readModel);
    }

    public async Task<List<B2BOrderSummary>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
    {
        var readModels = await dbContext.B2BOrderReadModels
            .AsNoTracking()
            .Where(b => b.CustomerId == customerId)
            .OrderByDescending(b => b.StartedAt)
            .ToListAsync(ct);

        return readModels.Select(MapToSummary).ToList();
    }

    public async Task<List<B2BOrderSummary>> GetByCompanyNameAsync(string companyName, CancellationToken ct = default)
    {
        var readModels = await dbContext.B2BOrderReadModels
            .AsNoTracking()
            .Where(b => EF.Functions.ILike(b.CompanyName, $"%{companyName}%"))
            .OrderByDescending(b => b.StartedAt)
            .ToListAsync(ct);

        return readModels.Select(MapToSummary).ToList();
    }

    public async Task<List<B2BOrderSummary>> GetAllAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
    {
        var readModels = await dbContext.B2BOrderReadModels
            .AsNoTracking()
            .OrderByDescending(b => b.StartedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return readModels.Select(MapToSummary).ToList();
    }

    // ── Private mappers ───────────────────────────────────────────────────────

    private static B2BOrderDetail MapToDetail(B2BOrderReadModel model)
    {
        var items = JsonSerializer.Deserialize<List<B2BLineItemResponse>>(model.ItemsJson)
                    ?? new List<B2BLineItemResponse>();
        var comments = JsonSerializer.Deserialize<List<string>>(model.CommentsJson)
                       ?? new List<string>();

        return new B2BOrderDetail(
            Id: model.Id,
            CustomerId: model.CustomerId,
            CompanyName: model.CompanyName,
            Status: model.Status,
            TotalPrice: model.TotalPrice,
            Currency: model.Currency,
            DiscountPercent: model.DiscountPercent,
            RequestedDeliveryDate: model.RequestedDeliveryDate,
            FinalizedOrderId: model.FinalizedOrderId,
            DeliveryAddress: new AddressResponse(
                model.DeliveryStreet,
                model.DeliveryCity,
                model.DeliveryCountry,
                model.DeliveryPostalCode),
            Items: items,
            Comments: comments,
            StartedAt: model.StartedAt,
            UpdatedAt: model.UpdatedAt,
            Version: model.Version,
            CompanyId: model.CompanyId);
    }

    private static B2BOrderSummary MapToSummary(B2BOrderReadModel model) =>
        new(
            Id: model.Id,
            CompanyName: model.CompanyName,
            Status: model.Status,
            TotalPrice: model.TotalPrice,
            Currency: model.Currency,
            StartedAt: model.StartedAt);
}
