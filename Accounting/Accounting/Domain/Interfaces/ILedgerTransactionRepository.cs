using Domain.Entities;

namespace Domain.Interfaces;

public interface ILedgerTransactionRepository
{
    Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LedgerTransaction?> GetByTransactionRefAsync(string transactionRef, CancellationToken cancellationToken = default);

    Task AddAsync(LedgerTransaction transaction, CancellationToken cancellationToken = default);
}
