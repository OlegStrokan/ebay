using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

internal sealed class OutboundMoneyEventRepository(PaymentDbContext dbContext) : IOutboundMoneyEventRepository
{
    public async Task<OutboundMoneyEvent?> GetByEventIdAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.OutboundMoneyEvents
            .FirstOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
    }

    public async Task<IReadOnlyList<OutboundMoneyEvent>> GetPendingAsync(
        DateTime now,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var take = maxCount <= 0 ? 100 : maxCount;

        return await dbContext.OutboundMoneyEvents
            .Where(x =>
                (x.Status == CallbackDeliveryStatus.Pending
                 || x.Status == CallbackDeliveryStatus.Failed)
                && (x.NextRetryAt == null || x.NextRetryAt <= now))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.NextRetryAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(OutboundMoneyEvent moneyEvent, CancellationToken cancellationToken = default)
    {
        await dbContext.OutboundMoneyEvents.AddAsync(moneyEvent, cancellationToken);
    }

    public Task UpdateAsync(OutboundMoneyEvent moneyEvent, CancellationToken cancellationToken = default)
    {
        var entry = dbContext.Entry(moneyEvent);
        if (entry.State == EntityState.Detached)
        {
            dbContext.OutboundMoneyEvents.Update(moneyEvent);
        }

        return Task.CompletedTask;
    }
}
