namespace Domain.Exceptions;

public sealed class DuplicateLedgerTransactionException(string transactionRef, Exception? inner = null)
    : Exception($"A ledger transaction already exists for ref '{transactionRef}'.", inner)
{
    public string TransactionRef { get; } = transactionRef;
}
