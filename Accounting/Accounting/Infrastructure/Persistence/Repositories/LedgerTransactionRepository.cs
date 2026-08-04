using Domain.Entities;
using Domain.Interfaces;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

internal sealed class LedgerTransactionRepository(AccountingDbContext dbContext) : ILedgerTransactionRepository
{
    public async Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await dbContext.LedgerTransactions
            .Include(x => x.Entries)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<LedgerTransaction?> GetByTransactionRefAsync(
        string transactionRef,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LedgerTransactions
            .Include(x => x.Entries)
            .FirstOrDefaultAsync(x => x.TransactionRef == transactionRef, cancellationToken);
    }

    public async Task AddAsync(LedgerTransaction transaction, CancellationToken cancellationToken = default)
    {
        await dbContext.LedgerTransactions.AddAsync(transaction, cancellationToken);
    }
}
