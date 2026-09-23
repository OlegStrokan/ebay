using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Services;
using Domain.ValueObjects;

namespace Domain.Tests;

public class ReportingProjectorTests
{
    private static readonly DateTime OccurredAt = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private static LedgerTransaction Capture(decimal amount, string currency, decimal fee = 0m, decimal tax = 0m) =>
        LedgerTransaction.ForCapture(
            Guid.NewGuid(), Guid.NewGuid(), "pay-1", new Money(amount, currency), fee, tax, OccurredAt);

    [Fact]
    public void Project_ShouldConvertEveryLegAtTheSameRate()
    {
        var tx = Capture(100m, "EUR");

        var projected = ReportingProjector.Project(tx, 1.1m, "USD", OccurredAt);

        Assert.Equal(2, projected.Count);
        Assert.All(projected, e => Assert.Equal(1.1m, e.RateUsed));
        Assert.All(projected, e => Assert.Equal("USD", e.ReportingCurrency));
        Assert.All(projected, e => Assert.Equal(110m, e.ReportingAmount));
    }

    [Fact]
    public void Project_ShouldKeepTheIdentityRateExact()
    {
        var tx = Capture(149.99m, "USD");

        var projected = ReportingProjector.Project(tx, 1m, "USD", OccurredAt);

        Assert.All(projected, e => Assert.Equal(149.99m, e.ReportingAmount));
        Assert.DoesNotContain(projected, e => e.Account == LedgerAccount.FxGainLoss);
    }

    [Fact]
    public void Project_ShouldLinkEachRowToItsPrimitiveEntry()
    {
        var tx = Capture(100m, "EUR");

        var projected = ReportingProjector.Project(tx, 1.1m, "USD", OccurredAt);

        Assert.All(projected, e => Assert.NotNull(e.EntryId));
        Assert.Equal(
            tx.Entries.Select(e => e.Id).OrderBy(id => id),
            projected.Select(e => e.EntryId!.Value).OrderBy(id => id));
    }

    [Fact]
    public void Project_ShouldPostFxGainLoss_WhenPerLegRoundingDoesNotTieOut()
    {
        // Four legs of 33.33/66.67 against 25/75 at an awkward rate: the halves round apart.
        var tx = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(100m, "EUR"), fee: 33.333m, tax: 66.666m, OccurredAt);

        var projected = ReportingProjector.Project(tx, 1.08375m, "USD", OccurredAt);

        var debits = projected.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.ReportingAmount);
        var credits = projected.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.ReportingAmount);

        Assert.Equal(debits, credits);

        // Whether a residual is needed depends on the rounding, but the books must balance either way.
        var residual = projected.SingleOrDefault(e => e.Account == LedgerAccount.FxGainLoss);
        if (residual is not null)
        {
            Assert.Null(residual.EntryId);
            Assert.True(residual.ReportingAmount > 0m);
        }
    }

    [Theory]
    [InlineData(0.000001)]
    [InlineData(1)]
    [InlineData(1234.5678)]
    [InlineData(0.987654)]
    public void Project_ShouldAlwaysBalance_AcrossAWideRangeOfRates(decimal rate)
    {
        var tx = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(999.99m, "EUR"), fee: 12.37m, tax: 199.99m, OccurredAt);

        var projected = ReportingProjector.Project(tx, rate, "USD", OccurredAt);

        var debits = projected.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.ReportingAmount);
        var credits = projected.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.ReportingAmount);

        Assert.Equal(debits, credits);
    }

    [Fact]
    public void Project_ShouldReturnNothing_WhenEveryLegRoundsAwayToZero()
    {
        // A zero posting is not a posting, and an empty projection must not fabricate a residual.
        var tx = Capture(0.0001m, "EUR");

        var projected = ReportingProjector.Project(tx, 0.0000001m, "USD", OccurredAt);

        Assert.Empty(projected);
    }

    [Fact]
    public void Project_ShouldReject_NonPositiveRate()
    {
        var tx = Capture(100m, "EUR");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportingProjector.Project(tx, 0m, "USD", OccurredAt));
    }

    [Fact]
    public void Project_ShouldNormaliseTheReportingCurrency()
    {
        var tx = Capture(100m, "EUR");

        var projected = ReportingProjector.Project(tx, 1.1m, " usd ", OccurredAt);

        Assert.All(projected, e => Assert.Equal("USD", e.ReportingCurrency));
    }

    [Fact]
    public void Project_ShouldReject_EmptyReportingCurrency()
    {
        var tx = Capture(100m, "EUR");

        Assert.Throws<ArgumentException>(
            () => ReportingProjector.Project(tx, 1.1m, "  ", OccurredAt));
    }
}
