using Domain.Entities;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Queries;
using Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests;

public class LedgerReportingTests : IDisposable
{
    private static readonly DateTime OccurredAt = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private readonly AccountingDbContext _dbContext = new(
        new DbContextOptionsBuilder<AccountingDbContext>()
            .UseInMemoryDatabase($"ledger-reporting-{Guid.NewGuid()}")
            .Options);

    private LedgerReportingReader Reader() => new(_dbContext);
    private LedgerReportingEntryRepository ReportingRepository() => new(_dbContext);
    private FxRateRepository FxRates() => new(_dbContext);

    private async Task SeedAsync(params LedgerTransaction[] transactions)
    {
        _dbContext.LedgerTransactions.AddRange(transactions);
        await _dbContext.SaveChangesAsync();
    }

    private async Task ProjectAsync(LedgerTransaction transaction, decimal rate)
    {
        var entries = ReportingProjector.Project(transaction, rate, "USD", OccurredAt);
        await ReportingRepository().AddRangeAsync(entries);
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task GetUnprojectedTransactions_ShouldReturnOnlyWhatHasNoReportingRowsYet()
    {
        var projected = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(100m, "USD"), 0m, 0m, OccurredAt);
        var pending = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-2", new Money(50m, "USD"), 0m, 0m, OccurredAt.AddMinutes(1));

        await SeedAsync(projected, pending);
        await ProjectAsync(projected, 1m);

        var unprojected = await ReportingRepository().GetUnprojectedTransactionsAsync(10);

        Assert.Equal(pending.Id, Assert.Single(unprojected).Id);
    }

    [Fact]
    public async Task GetUnprojectedTransactions_ShouldIncludeEntries_SoTheProjectorCanRun()
    {
        var transaction = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(100m, "USD"), 0m, 0m, OccurredAt);
        await SeedAsync(transaction);

        var unprojected = Assert.Single(await ReportingRepository().GetUnprojectedTransactionsAsync(10));

        Assert.NotEmpty(unprojected.Entries);
    }

    [Fact]
    public async Task GetEffectiveRate_ShouldReturnTheLatestRateAtOrBeforeTheDate()
    {
        _dbContext.FxRates.AddRange(
            FxRate.Create("EUR", "USD", 1.05m, OccurredAt.AddDays(-10)),
            FxRate.Create("EUR", "USD", 1.09m, OccurredAt.AddDays(-1)),
            FxRate.Create("EUR", "USD", 1.20m, OccurredAt.AddDays(5)));
        await _dbContext.SaveChangesAsync();

        var rate = await FxRates().GetEffectiveRateAsync("EUR", "USD", OccurredAt);

        // The future rate must not be used to convert a past transaction.
        Assert.Equal(1.09m, rate!.Rate);
    }

    [Fact]
    public async Task GetEffectiveRate_ShouldReturnNull_WhenNoRatePrecedesTheDate()
    {
        _dbContext.FxRates.Add(FxRate.Create("EUR", "USD", 1.20m, OccurredAt.AddDays(5)));
        await _dbContext.SaveChangesAsync();

        Assert.Null(await FxRates().GetEffectiveRateAsync("EUR", "USD", OccurredAt));
    }

    [Fact]
    public async Task GetTrialBalance_ShouldBalanceInTheReportingCurrency()
    {
        var orderId = Guid.NewGuid();
        var capture = LedgerTransaction.ForCapture(
            orderId, null, "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt);
        var refund = LedgerTransaction.ForRefund(orderId, "ref-1", new Money(40m, "EUR"), OccurredAt.AddMinutes(1));

        await SeedAsync(capture, refund);
        await ProjectAsync(capture, 1.1m);
        await ProjectAsync(refund, 1.1m);

        var trialBalance = await Reader().GetTrialBalanceAsync("USD");

        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(154m, trialBalance.ReportingDebits);
        Assert.Equal(154m, trialBalance.ReportingCredits);

        // customer_captured: debited 110 by the capture, credited 44 by the refund.
        var captured = trialBalance.ReportingCurrencyBalances
            .Single(b => b.Account == LedgerAccount.CustomerCaptured);
        Assert.Equal(66m, captured.Balance);
    }

    [Fact]
    public async Task GetTrialBalance_ShouldKeepTransactionCurrencyBalancesSeparate()
    {
        await SeedAsync(
            LedgerTransaction.ForCapture(Guid.NewGuid(), null, "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt),
            LedgerTransaction.ForCapture(Guid.NewGuid(), null, "pay-2", new Money(70m, "USD"), 0m, 0m, OccurredAt));

        var trialBalance = await Reader().GetTrialBalanceAsync("USD");

        var currencies = trialBalance.TransactionCurrencyBalances.Select(b => b.Currency).Distinct().ToList();
        Assert.Equal(2, currencies.Count);
        Assert.Contains("EUR", currencies);
        Assert.Contains("USD", currencies);
    }

    [Fact]
    public async Task GetOrderMoneyTrail_ShouldReturnEveryLegInTheOrderItHappened()
    {
        var orderId = Guid.NewGuid();
        await SeedAsync(
            LedgerTransaction.ForRefund(orderId, "ref-1", new Money(40m, "EUR"), OccurredAt.AddMinutes(2)),
            LedgerTransaction.ForAuthorization(orderId, null, "pay-1", new Money(100m, "EUR"), OccurredAt),
            LedgerTransaction.ForCapture(orderId, null, "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt.AddMinutes(1)));

        var trail = await Reader().GetOrderMoneyTrailAsync(orderId);

        Assert.Equal(
            new[] { TransactionRefType.Authorize, TransactionRefType.Capture, TransactionRefType.Refund },
            trail.Select(t => t.RefType));
        Assert.All(trail, t => Assert.Equal(2, t.Entries.Count));
    }

    [Fact]
    public async Task GetOrderMoneyTrail_ShouldReturnNothing_ForAnUnknownOrder()
    {
        await SeedAsync(LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(100m, "EUR"), 0m, 0m, OccurredAt));

        Assert.Empty(await Reader().GetOrderMoneyTrailAsync(Guid.NewGuid()));
    }

    public void Dispose() => _dbContext.Dispose();
}
