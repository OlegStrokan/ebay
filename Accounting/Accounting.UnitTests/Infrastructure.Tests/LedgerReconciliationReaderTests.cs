using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests;

public class LedgerReconciliationReaderTests : IDisposable
{
    private static readonly DateTime OccurredAt = new(2026, 8, 31, 10, 0, 0, DateTimeKind.Utc);

    private readonly AccountingDbContext _dbContext = new(
        new DbContextOptionsBuilder<AccountingDbContext>()
            .UseInMemoryDatabase($"ledger-recon-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private LedgerReconciliationReader Reader() => new(_dbContext);

    private async Task SeedAsync(params LedgerTransaction[] transactions)
    {
        _dbContext.LedgerTransactions.AddRange(transactions);
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task GetCurrencyBalances_ShouldBalance_WhenEveryPostingIsWellFormed()
    {
        var orderId = Guid.NewGuid();
        await SeedAsync(
            LedgerTransaction.ForCapture(orderId, Guid.NewGuid(), "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt),
            LedgerTransaction.ForRefund(orderId, "ref-1", new Money(40m, "EUR"), OccurredAt));

        var balances = await Reader().GetCurrencyBalancesAsync();

        var eur = Assert.Single(balances);
        Assert.Equal("EUR", eur.Currency);
        Assert.True(eur.IsBalanced);
        Assert.Equal(0m, eur.Drift);
    }

    [Fact]
    public async Task GetCurrencyBalances_ShouldReportEachCurrencySeparately()
    {
        await SeedAsync(
            LedgerTransaction.ForCapture(Guid.NewGuid(), null, "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt),
            LedgerTransaction.ForCapture(Guid.NewGuid(), null, "pay-2", new Money(70m, "USD"), 0m, 0m, OccurredAt));

        var balances = await Reader().GetCurrencyBalancesAsync();

        Assert.Equal(2, balances.Count);
        Assert.All(balances, b => Assert.True(b.IsBalanced));
        Assert.Equal(100m, balances.Single(b => b.Currency == "EUR").Debits);
        Assert.Equal(70m, balances.Single(b => b.Currency == "USD").Debits);
    }

    [Fact]
    public async Task GetOverRefundedOrders_ShouldFlagADoubleRefund()
    {
        // Regression proof for the compensation-retry double-refund. Two refunds, each a valid
        // balanced posting with its own transaction_ref, together exceeding what was captured.
        var orderId = Guid.NewGuid();
        await SeedAsync(
            LedgerTransaction.ForCapture(orderId, Guid.NewGuid(), "pay-1", new Money(4180m, "USD"), 0m, 0m, OccurredAt),
            LedgerTransaction.ForRefund(orderId, "ref-1", new Money(4180m, "USD"), OccurredAt),
            LedgerTransaction.ForRefund(orderId, "ref-2", new Money(4180m, "USD"), OccurredAt));

        // The aggregate invariant alone does NOT catch it: both refunds are internally balanced.
        var balances = await Reader().GetCurrencyBalancesAsync();
        Assert.True(Assert.Single(balances).IsBalanced);

        var overRefunded = Assert.Single(await Reader().GetOverRefundedOrdersAsync(20));

        Assert.Equal(orderId, overRefunded.OrderId);
        Assert.Equal("USD", overRefunded.Currency);
        Assert.Equal(4180m, overRefunded.Captured);
        Assert.Equal(8360m, overRefunded.Refunded);
    }

    [Fact]
    public async Task GetOverRefundedOrders_ShouldStaySilent_ForAPartialRefund()
    {
        var orderId = Guid.NewGuid();
        await SeedAsync(
            LedgerTransaction.ForCapture(orderId, Guid.NewGuid(), "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt),
            LedgerTransaction.ForRefund(orderId, "ref-1", new Money(30m, "EUR"), OccurredAt),
            LedgerTransaction.ForRefund(orderId, "ref-2", new Money(30m, "EUR"), OccurredAt));

        Assert.Empty(await Reader().GetOverRefundedOrdersAsync(20));
    }

    [Fact]
    public async Task GetOverRefundedOrders_ShouldNotMixCurrencies()
    {
        var orderId = Guid.NewGuid();
        await SeedAsync(
            LedgerTransaction.ForCapture(orderId, Guid.NewGuid(), "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt),
            LedgerTransaction.ForRefund(orderId, "ref-usd", new Money(50m, "USD"), OccurredAt));

        // A USD refund against a EUR capture is over-refunded in USD, and must not be netted off
        // against the EUR capture.
        var flagged = Assert.Single(await Reader().GetOverRefundedOrdersAsync(20));
        Assert.Equal("USD", flagged.Currency);
        Assert.Equal(0m, flagged.Captured);
    }

    [Fact]
    public async Task GetUnbalancedTransactions_ShouldFindNothing_WhenPostingsComeFromTheFactories()
    {
        await SeedAsync(
            LedgerTransaction.ForAuthorization(Guid.NewGuid(), null, "pay-1", new Money(10m, "EUR"), OccurredAt),
            LedgerTransaction.ForCapture(Guid.NewGuid(), null, "pay-2", new Money(10m, "EUR"), 1m, 2m, OccurredAt));

        Assert.Empty(await Reader().GetUnbalancedTransactionsAsync(20));
    }

    [Fact]
    public async Task GetUnbalancedTransactions_ShouldFindACorruptedRow()
    {
        // The factories cannot produce this; a direct DB write or a migration slip can.
        var capture = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt);
        await SeedAsync(capture);

        var credit = await _dbContext.LedgerEntries
            .FirstAsync(e => e.Direction == Domain.Enums.EntryDirection.Credit);
        _dbContext.Entry(credit).Property(e => e.Amount).CurrentValue = 90m;
        await _dbContext.SaveChangesAsync();

        var broken = Assert.Single(await Reader().GetUnbalancedTransactionsAsync(20));

        Assert.Equal(capture.Id, broken.TransactionId);
        Assert.Equal("capture:pay-1", broken.TransactionRef);
        Assert.Equal(100m, broken.Debits);
        Assert.Equal(90m, broken.Credits);
    }

    public void Dispose() => _dbContext.Dispose();
}
