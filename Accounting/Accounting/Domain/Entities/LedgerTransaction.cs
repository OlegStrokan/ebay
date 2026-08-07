using Domain.Enums;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Entities;


public sealed class LedgerTransaction
{
    private readonly List<LedgerEntry> _entries = [];
    public Guid Id { get; private set; }
    public string TransactionRef { get; private set; } = null!;
    public Guid? OrderId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public TransactionRefType RefType { get; private set; }
    public string RefId { get; private set; } = null!;
    public string Currency { get; private set; } = null!;
    public DateTime OccurredAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public IReadOnlyCollection<LedgerEntry> Entries => _entries.AsReadOnly();

    private LedgerTransaction()
    {
    }

    private LedgerTransaction(
        string transactionRef,
        TransactionRefType refType,
        string refId,
        string currency,
        Guid? orderId,
        Guid? paymentId,
        DateTime occurredAt)
    {
        Id = Guid.NewGuid();
        TransactionRef = transactionRef;
        RefType = refType;
        RefId = refId;
        Currency = currency;
        OrderId = orderId;
        PaymentId = paymentId;
        OccurredAt = occurredAt;
        CreatedAt = DateTime.UtcNow;
    }

    // Pays out the refund liability in cash: Dr refunds_payable / Cr customer_captured
    // our dept actually paid
    public static LedgerTransaction ForRefund(Guid orderId, string refundId, Money amount, DateTime occurredAt)
    {
        if (string.IsNullOrWhiteSpace(refundId))
            throw new ArgumentException("RefundId is required.", nameof(refundId));

        var tx = new LedgerTransaction(
            transactionRef: $"refund:{refundId}",
            refType: TransactionRefType.Refund,
            refId: refundId,
            currency: amount.Currency,
            orderId: orderId,
            paymentId: null,
            occurredAt: occurredAt);

        tx.AddEntry(LedgerAccount.RefundsPayable, EntryDirection.Debit, amount);
        tx.AddEntry(LedgerAccount.CustomerCaptured, EntryDirection.Credit, amount);
        tx.EnsureBalanced();
        return tx;
    }

    // Takes back previously recognized revenue on a return: Dr merchant_revenue / Cr refunds_payable
    // we now owe this amount back to the customer
    public static LedgerTransaction ForRevenueReversal(
        Guid orderId,
        Guid returnRequestId,
        Money amount,
        DateTime occurredAt)
    {
        // An order can be returned more than once, so only the return request identifies a reversal
        if (returnRequestId == Guid.Empty)
            throw new ArgumentException("ReturnRequestId is required.", nameof(returnRequestId));

        var tx = new LedgerTransaction(
            transactionRef: $"reversal:{returnRequestId}",
            refType: TransactionRefType.Reversal,
            refId: returnRequestId.ToString(),
            currency: amount.Currency,
            orderId: orderId,
            paymentId: null,
            occurredAt: occurredAt);

        tx.AddEntry(LedgerAccount.MerchantRevenue, EntryDirection.Debit, amount);
        tx.AddEntry(LedgerAccount.RefundsPayable, EntryDirection.Credit, amount);
        tx.EnsureBalanced();
        return tx;
    }

    //append-only reversing transaction that cancels a prior revenue reversal by swapping every leg.
    public static LedgerTransaction ForReversalCancellation(LedgerTransaction original, DateTime occurredAt)
    {
        if (original.RefType != TransactionRefType.Reversal)
            throw new InvalidOperationException(
                $"Transaction {original.Id} is not a revenue reversal and cannot be cancelled.");

        var tx = new LedgerTransaction(
            transactionRef: $"cancel-reversal:{original.Id}",
            refType: TransactionRefType.ReversalCancellation,
            refId: original.Id.ToString(),
            currency: original.Currency,
            orderId: original.OrderId,
            paymentId: original.PaymentId,
            occurredAt: occurredAt);

        foreach (var entry in original._entries)
        {
            var reversed = entry.Direction == EntryDirection.Debit
                ? EntryDirection.Credit
                : EntryDirection.Debit;

            tx.AddEntry(entry.Account, reversed, new Money(entry.Amount, entry.Currency));
        }

        tx.EnsureBalanced();
        return tx;
    }

    private void AddEntry(LedgerAccount account, EntryDirection direction, Money amount)
    {
        _entries.Add(new LedgerEntry(Id, account, direction, amount.Amount, amount.Currency, CreatedAt));
    }

    private void EnsureBalanced()
    {
        if (_entries.Count == 0)
            throw new UnbalancedTransactionException($"Transaction {TransactionRef} has no entries.");

        foreach (var group in _entries.GroupBy(e => e.Currency))
        {
            var debits = group.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.Amount);
            var credits = group.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.Amount);

            if (debits != credits)
                throw new UnbalancedTransactionException(
                    $"Transaction {TransactionRef} is unbalanced for {group.Key}: debits={debits}, credits={credits}.");
        }
    }
}
