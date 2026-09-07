namespace Application.Interfaces;

public sealed record CurrencyBalance(string Currency, decimal Debits, decimal Credits)
{
    public decimal Drift => Debits - Credits;

    public bool IsBalanced => Debits == Credits;
}

public sealed record UnbalancedTransaction(
    Guid TransactionId,
    string TransactionRef,
    string Currency,
    decimal Debits,
    decimal Credits);

// Refunds credit customer_captured, captures debit it. Credits exceeding debits means more money
// went back to the customer than was ever taken - the double-refund class.
public sealed record OverRefundedOrder(
    Guid OrderId,
    string Currency,
    decimal Captured,
    decimal Refunded);

public interface ILedgerReconciliationReader
{
    Task<IReadOnlyList<CurrencyBalance>> GetCurrencyBalancesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UnbalancedTransaction>> GetUnbalancedTransactionsAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OverRefundedOrder>> GetOverRefundedOrdersAsync(
        int maxCount,
        CancellationToken cancellationToken = default);
}
