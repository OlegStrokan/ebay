using Domain.Entities;

namespace Domain.Interfaces;

public interface IOutboundMoneyEventRepository
{
    Task<OutboundMoneyEvent?> GetByEventIdAsync(string eventId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboundMoneyEvent>> GetPendingAsync(
        DateTime now,
        int maxCount,
        CancellationToken cancellationToken = default);

    Task AddAsync(OutboundMoneyEvent moneyEvent, CancellationToken cancellationToken = default);

    Task UpdateAsync(OutboundMoneyEvent moneyEvent, CancellationToken cancellationToken = default);
}
