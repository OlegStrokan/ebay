using System.Text.Json;
using Domain.Events.B2BOrder;
using Infrastructure.Persistence.DbContext;
using Infrastructure.ReadModels;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class B2BOrderReadModelUpdater(ReadDbContext dbContext, ILogger<B2BOrderReadModelUpdater> logger)
    : IReadModelUpdater
{
    private static readonly HashSet<Type> _handledTypes =
    [
        typeof(B2BOrderStartedEvent),
        typeof(LineItemAddedEvent),
        typeof(LineItemRemovedEvent),
        typeof(LineItemQuantityChangedEvent),
        typeof(LineItemPriceAdjustedEvent),
        typeof(DiscountAppliedEvent),
        typeof(CommentAddedEvent),
        typeof(DeliveryDateChangedEvent),
        typeof(DeliveryAddressChangedEvent),
        typeof(QuoteFinalizedEvent),
        typeof(B2BOrderCancelledEvent)
    ];

    public bool CanHandle(Type eventType) => _handledTypes.Contains(eventType);

    public async Task HandleAsync(B2BOrderStartedEvent evt, CancellationToken ct = default)
    {
        var exists = await dbContext.B2BOrderReadModels
            .AnyAsync(b => b.Id == evt.B2BOrderId.Value, ct);

        if (exists)
        {
            logger.LogInformation(
                "B2BOrderReadModel {B2BOrderId} already exists. Skipping duplicate.", evt.B2BOrderId.Value);
            return;
        }

        var readModel = new B2BOrderReadModel
        {
            Id = evt.B2BOrderId.Value,
            CustomerId = evt.CustomerId.Value,
            CompanyId = evt.CompanyId,
            CompanyName = evt.CompanyName,
            Status = "Draft",
            TotalPrice = 0,
            Currency = "USD",
            DiscountPercent = 0,
            DeliveryStreet = evt.DeliveryAddress.Street,
            DeliveryCity = evt.DeliveryAddress.City,
            DeliveryCountry = evt.DeliveryAddress.Country,
            DeliveryPostalCode = evt.DeliveryAddress.PostalCode,
            ItemsJson = "[]",
            CommentsJson = "[]",
            StartedAt = evt.OccurredAt,
            Version = 0,
            LastSyncedAt = DateTime.UtcNow
        };

        dbContext.B2BOrderReadModels.Add(readModel);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Created B2BOrderReadModel for {B2BOrderId}", evt.B2BOrderId.Value);
    }


    public async Task HandleAsync(LineItemAddedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        var items = DeserializeItems(model.ItemsJson);
        items.Add(new B2BLineItemRecord(
            LineItemId: evt.LineItemId.Value,
            ProductId: evt.ProductId.Value,
            Quantity: evt.Quantity,
            UnitPrice: evt.UnitPrice.Amount,
            AdjustedUnitPrice: null,
            Currency: evt.UnitPrice.Currency,
            IsRemoved: false));

        model.ItemsJson = JsonSerializer.Serialize(items);
        model.TotalPrice = ComputeTotal(items, model.DiscountPercent);
        model.Currency = items.FirstOrDefault(i => !i.IsRemoved)?.Currency ?? model.Currency;
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(LineItemRemovedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        var items = DeserializeItems(model.ItemsJson);
        var idx = items.FindIndex(i => i.LineItemId == evt.LineItemId.Value);
        if (idx >= 0) items[idx] = items[idx] with { IsRemoved = true };

        model.ItemsJson = JsonSerializer.Serialize(items);
        model.TotalPrice = ComputeTotal(items, model.DiscountPercent);
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(LineItemQuantityChangedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        var items = DeserializeItems(model.ItemsJson);
        var idx = items.FindIndex(i => i.LineItemId == evt.LineItemId.Value);
        if (idx >= 0) items[idx] = items[idx] with { Quantity = evt.NewQuantity };

        model.ItemsJson = JsonSerializer.Serialize(items);
        model.TotalPrice = ComputeTotal(items, model.DiscountPercent);
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(LineItemPriceAdjustedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        var items = DeserializeItems(model.ItemsJson);
        var idx = items.FindIndex(i => i.LineItemId == evt.LineItemId.Value);
        if (idx >= 0) items[idx] = items[idx] with { AdjustedUnitPrice = evt.NewPrice.Amount };

        model.ItemsJson = JsonSerializer.Serialize(items);
        model.TotalPrice = ComputeTotal(items, model.DiscountPercent);
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }
    
    public async Task HandleAsync(DiscountAppliedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        model.DiscountPercent = evt.DiscountPercent;
        var items = DeserializeItems(model.ItemsJson);
        model.TotalPrice = ComputeTotal(items, evt.DiscountPercent);
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(CommentAddedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        var comments = JsonSerializer.Deserialize<List<string>>(model.CommentsJson) ?? new();
        comments.Add($"[{evt.Author} @ {evt.OccurredAt:u}] {evt.Text}");
        model.CommentsJson = JsonSerializer.Serialize(comments);
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(DeliveryDateChangedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        model.RequestedDeliveryDate = evt.NewDeliveryDate;
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(DeliveryAddressChangedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        model.DeliveryStreet = evt.NewAddress.Street;
        model.DeliveryCity = evt.NewAddress.City;
        model.DeliveryCountry = evt.NewAddress.Country;
        model.DeliveryPostalCode = evt.NewAddress.PostalCode;
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }
    
    public async Task HandleAsync(QuoteFinalizedEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        model.Status = "Finalized";
        model.FinalizedOrderId = evt.FinalizedOrderId;
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(B2BOrderCancelledEvent evt, CancellationToken ct = default)
    {
        var model = await GetReadModelAsync(evt.B2BOrderId.Value, ct);
        if (model is null) return;

        model.Status = "Cancelled";
        model.UpdatedAt = evt.OccurredAt;
        model.Version++;
        model.LastSyncedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }
    
    private async Task<B2BOrderReadModel?> GetReadModelAsync(Guid b2bOrderId, CancellationToken ct)
    {
        var model = await dbContext.B2BOrderReadModels
            .FirstOrDefaultAsync(b => b.Id == b2bOrderId, ct);

        if (model is null)
        {
            logger.LogWarning(
                "B2BOrderReadModel not found for {B2BOrderId}. May need to rebuild from events.", b2bOrderId);
        }

        return model;
    }

    private static List<B2BLineItemRecord> DeserializeItems(string json) =>
        JsonSerializer.Deserialize<List<B2BLineItemRecord>>(json) ?? new();

    private static decimal ComputeTotal(List<B2BLineItemRecord> items, decimal discountPercent)
    {
        var active = items.Where(i => !i.IsRemoved).ToList();
        if (!active.Any()) return 0m;

        var subtotal = active.Sum(i => (i.AdjustedUnitPrice ?? i.UnitPrice) * i.Quantity);

        return discountPercent > 0
            ? Math.Round(subtotal * (1 - discountPercent / 100m), 2)
            : Math.Round(subtotal, 2);
    }

    private sealed record B2BLineItemRecord(
        Guid LineItemId,
        Guid ProductId,
        int Quantity,
        decimal UnitPrice,
        decimal? AdjustedUnitPrice,
        string Currency,
        bool IsRemoved);
}
