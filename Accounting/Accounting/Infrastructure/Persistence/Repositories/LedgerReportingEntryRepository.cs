using Domain.Entities;
using Domain.Interfaces;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

internal sealed class LedgerReportingEntryRepository(AccountingDbContext dbContext)
    : ILedgerReportingEntryRepository
{
    public async Task<IReadOnlyList<LedgerTransaction>> GetUnprojectedTransactionsAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var take = maxCount <= 0 ? 200 : maxCount;

        return await dbContext.LedgerTransactions
            .Include(t => t.Entries)
            .Where(t => !dbContext.LedgerReportingEntries.Any(r => r.TransactionId == t.Id))
            .OrderBy(t => t.OccurredAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task AddRangeAsync(
        IEnumerable<LedgerReportingEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await dbContext.LedgerReportingEntries.AddRangeAsync(entries, cancellationToken);
    }
}
