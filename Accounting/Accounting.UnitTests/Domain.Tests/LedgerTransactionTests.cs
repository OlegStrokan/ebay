using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Tests;

public class LedgerTransactionTests
{
    [Fact]
    public void ForRefund_ShouldPostBalancedDebitAndCredit()
    {
        var orderId = Guid.NewGuid();
        var tx = LedgerTransaction.ForRefund(orderId, "REF-123", new Money(100m, "EUR"), DateTime.UtcNow);

        Assert.Equal(TransactionRefType.Refund, tx.RefType);
        Assert.Equal("refund:REF-123", tx.TransactionRef);
        Assert.Equal(2, tx.Entries.Count);

        var debit = Assert.Single(tx.Entries.Where(e => e.Direction == EntryDirection.Debit));
        var credit = Assert.Single(tx.Entries.Where(e => e.Direction == EntryDirection.Credit));

        Assert.Equal(LedgerAccount.RefundsPayable, debit.Account);
        Assert.Equal(LedgerAccount.CustomerCaptured, credit.Account);
        Assert.Equal(debit.Amount, credit.Amount);
    }

    [Fact]
    public void ForRevenueReversal_ShouldDebitRevenueAndCreditRefundsPayable()
    {
        var orderId = Guid.NewGuid();
        var returnRequestId = Guid.NewGuid();
        var tx = LedgerTransaction.ForRevenueReversal(orderId, returnRequestId, new Money(250.50m, "EUR"), DateTime.UtcNow);

        var debit = Assert.Single(tx.Entries.Where(e => e.Direction == EntryDirection.Debit));
        var credit = Assert.Single(tx.Entries.Where(e => e.Direction == EntryDirection.Credit));

        Assert.Equal(LedgerAccount.MerchantRevenue, debit.Account);
        Assert.Equal(LedgerAccount.RefundsPayable, credit.Account);
        Assert.Equal(250.50m, debit.Amount);
    }

    [Fact]
    public void ForRevenueReversal_ShouldKeyTransactionRefOnReturnRequest_NotAmount()
    {
        var orderId = Guid.NewGuid();
        var firstReturn = Guid.NewGuid();
        var secondReturn = Guid.NewGuid();

        var first = LedgerTransaction.ForRevenueReversal(orderId, firstReturn, new Money(50m, "USD"), DateTime.UtcNow);
        var second = LedgerTransaction.ForRevenueReversal(orderId, secondReturn, new Money(50m, "USD"), DateTime.UtcNow);

        Assert.Equal($"reversal:{firstReturn}", first.TransactionRef);
        Assert.NotEqual(first.TransactionRef, second.TransactionRef);
    }

    [Fact]
    public void ForRevenueReversal_ShouldThrow_WhenReturnRequestIdMissing()
    {
        Assert.Throws<ArgumentException>(() =>
            LedgerTransaction.ForRevenueReversal(Guid.NewGuid(), Guid.Empty, new Money(50m, "USD"), DateTime.UtcNow));
    }

    [Fact]
    public void ForReversalCancellation_ShouldSwapEveryLegOfOriginal()
    {
        var orderId = Guid.NewGuid();
        var original = LedgerTransaction.ForRevenueReversal(orderId, Guid.NewGuid(), new Money(80m, "USD"), DateTime.UtcNow);

        var cancellation = LedgerTransaction.ForReversalCancellation(original, DateTime.UtcNow);

        Assert.Equal(TransactionRefType.ReversalCancellation, cancellation.RefType);
        Assert.Equal($"cancel-reversal:{original.Id}", cancellation.TransactionRef);

        var debit = Assert.Single(cancellation.Entries.Where(e => e.Direction == EntryDirection.Debit));
        var credit = Assert.Single(cancellation.Entries.Where(e => e.Direction == EntryDirection.Credit));

        // Original debited revenue / credited refunds_payable → cancellation swaps them.
        Assert.Equal(LedgerAccount.RefundsPayable, debit.Account);
        Assert.Equal(LedgerAccount.MerchantRevenue, credit.Account);
    }

    [Fact]
    public void ForReversalCancellation_ShouldReject_WhenOriginalIsNotAReversal()
    {
        var refund = LedgerTransaction.ForRefund(Guid.NewGuid(), "REF-1", new Money(10m, "EUR"), DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(
            () => LedgerTransaction.ForReversalCancellation(refund, DateTime.UtcNow));
    }

    [Fact]
    public void Entry_ShouldReject_NonPositiveAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LedgerTransaction.ForRefund(Guid.NewGuid(), "REF-0", new Money(0m, "EUR"), DateTime.UtcNow));
    }
}
