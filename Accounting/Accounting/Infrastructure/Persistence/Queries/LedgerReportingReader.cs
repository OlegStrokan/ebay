using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Queries;

internal sealed class LedgerReportingReader(AccountingDbContext dbContext) : ILedgerReportingReader
{
    public async Task<TrialBalance> GetTrialBalanceAsync(
        string reportingCurrency,
        CancellationToken cancellationToken = default)
    {
        var currency = reportingCurrency.Trim().ToUpperInvariant();

        var native = await dbContext.LedgerEntries
            .GroupBy(e => new { e.Account, e.Currency })
            .Select(g => new
            {
                g.Key.Account,
                g.Key.Currency,
                Debits = g.Sum(e => e.Direction == EntryDirection.Debit ? e.Amount : 0m),
                Credits = g.Sum(e => e.Direction == EntryDirection.Credit ? e.Amount : 0m),
            })
            .ToListAsync(cancellationToken);

        var reporting = await dbContext.LedgerReportingEntries
            .Where(e => e.ReportingCurrency == currency)
            .GroupBy(e => e.Account)
            .Select(g => new
            {
                Account = g.Key,
                Debits = g.Sum(e => e.Direction == EntryDirection.Debit ? e.ReportingAmount : 0m),
                Credits = g.Sum(e => e.Direction == EntryDirection.Credit ? e.ReportingAmount : 0m),
            })
            .ToListAsync(cancellationToken);

        var reportingBalances = reporting
            .Select(r => new AccountBalance(r.Account, currency, r.Debits, r.Credits))
            .OrderBy(r => r.Account)
            .ToList();

        return new TrialBalance(
            currency,
            native
                .Select(n => new AccountBalance(n.Account, n.Currency, n.Debits, n.Credits))
                .OrderBy(n => n.Currency).ThenBy(n => n.Account)
                .ToList(),
            reportingBalances,
            reportingBalances.Sum(r => r.Debits),
            reportingBalances.Sum(r => r.Credits));
    }

    public async Task<IReadOnlyList<MoneyTrailTransaction>> GetOrderMoneyTrailAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var transactions = await dbContext.LedgerTransactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .Where(t => t.OrderId == orderId)
            .OrderBy(t => t.OccurredAt)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return transactions
            .Select(t => new MoneyTrailTransaction(
                t.Id,
                t.TransactionRef,
                t.RefType,
                t.RefId,
                t.Currency,
                t.OccurredAt,
                t.Entries
                    .Select(e => new MoneyTrailEntry(e.Account, e.Direction, e.Amount, e.Currency))
                    .ToList()))
            .ToList();
    }
}
