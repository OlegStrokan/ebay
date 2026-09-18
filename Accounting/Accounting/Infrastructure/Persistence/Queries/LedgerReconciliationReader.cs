using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Queries;

internal sealed class LedgerReconciliationReader(AccountingDbContext dbContext) : ILedgerReconciliationReader
{
    public async Task<IReadOnlyList<CurrencyBalance>> GetCurrencyBalancesAsync(
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LedgerEntries
            .GroupBy(e => e.Currency)
            .Select(g => new CurrencyBalance(
                g.Key,
                g.Sum(e => e.Direction == EntryDirection.Debit ? e.Amount : 0m),
                g.Sum(e => e.Direction == EntryDirection.Credit ? e.Amount : 0m)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnbalancedTransaction>> GetUnbalancedTransactionsAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var take = maxCount <= 0 ? 20 : maxCount;

        // Filtering after the projection is what EF turns into HAVING; a `let` inside the group
        // does not translate.
        var broken = await dbContext.LedgerEntries
            .GroupBy(e => new { e.TransactionId, e.Currency })
            .Select(g => new
            {
                g.Key.TransactionId,
                g.Key.Currency,
                Debits = g.Sum(e => e.Direction == EntryDirection.Debit ? e.Amount : 0m),
                Credits = g.Sum(e => e.Direction == EntryDirection.Credit ? e.Amount : 0m),
            })
            .Where(x => x.Debits != x.Credits)
            .Take(take)
            .ToListAsync(cancellationToken);

        if (broken.Count == 0)
            return [];

        var ids = broken.Select(b => b.TransactionId).ToList();
        var refs = await dbContext.LedgerTransactions
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.TransactionRef })
            .ToDictionaryAsync(t => t.Id, t => t.TransactionRef, cancellationToken);

        return broken
            .Select(b => new UnbalancedTransaction(
                b.TransactionId,
                refs.TryGetValue(b.TransactionId, out var reference) ? reference : "unknown",
                b.Currency,
                b.Debits,
                b.Credits))
            .ToList();
    }

    public async Task<IReadOnlyList<OverRefundedOrder>> GetOverRefundedOrdersAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var take = maxCount <= 0 ? 20 : maxCount;

        // Grouped by order rather than payment: the gRPC RecordRefund path posts without a
        // payment id, so grouping on payment would miss exactly the refunds worth checking.
        var legs =
            from entry in dbContext.LedgerEntries
            join transaction in dbContext.LedgerTransactions on entry.TransactionId equals transaction.Id
            where entry.Account == LedgerAccount.CustomerCaptured && transaction.OrderId != null
            select new { transaction.OrderId, entry.Currency, entry.Direction, entry.Amount };

        var overRefunded = await legs
            .GroupBy(x => new { x.OrderId, x.Currency })
            .Select(g => new
            {
                g.Key.OrderId,
                g.Key.Currency,
                Captured = g.Sum(x => x.Direction == EntryDirection.Debit ? x.Amount : 0m),
                Refunded = g.Sum(x => x.Direction == EntryDirection.Credit ? x.Amount : 0m),
            })
            .Where(x => x.Refunded > x.Captured)
            .Take(take)
            .ToListAsync(cancellationToken);

        return overRefunded
            .Select(x => new OverRefundedOrder(x.OrderId!.Value, x.Currency, x.Captured, x.Refunded))
            .ToList();
    }
}
