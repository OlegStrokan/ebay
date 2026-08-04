using Domain.Enums;

namespace Domain.Entities;


public sealed class LedgerEntry
{
    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public LedgerAccount Account { get; private set; }
    public EntryDirection Direction { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }
    private LedgerEntry()
    {
    }

    internal LedgerEntry(
        Guid transactionId,
        LedgerAccount account,
        EntryDirection direction,
        decimal amount,
        string currency,
        DateTime createdAt)
    {
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "Entry amount must be positive.");

        Id = Guid.NewGuid();
        TransactionId = transactionId;
        Account = account;
        Direction = direction;
        Amount = amount;
        Currency = currency;
        CreatedAt = createdAt;
    }
}
