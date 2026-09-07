using Domain.Enums;

namespace Application.Interfaces;

public sealed record AccountBalance(
    LedgerAccount Account,
    string Currency,
    decimal Debits,
    decimal Credits)
{
    public decimal Balance => Debits - Credits;
}

public sealed record TrialBalance(
    string ReportingCurrency,
    IReadOnlyList<AccountBalance> TransactionCurrencyBalances,
    IReadOnlyList<AccountBalance> ReportingCurrencyBalances,
    decimal ReportingDebits,
    decimal ReportingCredits)
{
    public bool IsBalanced => ReportingDebits == ReportingCredits;
}

public sealed record MoneyTrailEntry(
    LedgerAccount Account,
    EntryDirection Direction,
    decimal Amount,
    string Currency);

public sealed record MoneyTrailTransaction(
    Guid TransactionId,
    string TransactionRef,
    TransactionRefType RefType,
    string RefId,
    string Currency,
    DateTime OccurredAt,
    IReadOnlyList<MoneyTrailEntry> Entries);

public interface ILedgerReportingReader
{
    Task<TrialBalance> GetTrialBalanceAsync(
        string reportingCurrency,
        CancellationToken cancellationToken = default);

    // Authorize to capture to refund to reversal, in the order it happened.
    Task<IReadOnlyList<MoneyTrailTransaction>> GetOrderMoneyTrailAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
}
