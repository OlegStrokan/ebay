namespace Domain.Exceptions;

// Raised when a ledger transaction does not satisfy Σdebits = Σcredits per currency
public sealed class UnbalancedTransactionException(string message) : Exception(message);
