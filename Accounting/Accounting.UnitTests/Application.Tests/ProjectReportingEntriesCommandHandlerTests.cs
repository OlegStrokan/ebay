using Application.Commands.ProjectReportingEntries;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests;

public class ProjectReportingEntriesCommandHandlerTests
{
    private static readonly DateTime OccurredAt = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private readonly ILedgerReportingEntryRepository _reporting =
        Substitute.For<ILedgerReportingEntryRepository>();

    private readonly IFxRateRepository _fxRates = Substitute.For<IFxRateRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ProjectReportingEntriesCommandHandler _handler;

    private readonly List<LedgerReportingEntry> _written = [];

    public ProjectReportingEntriesCommandHandlerTests()
    {
        _reporting
            .When(r => r.AddRangeAsync(Arg.Any<IEnumerable<LedgerReportingEntry>>(), Arg.Any<CancellationToken>()))
            .Do(call => _written.AddRange(call.Arg<IEnumerable<LedgerReportingEntry>>()));

        _handler = new ProjectReportingEntriesCommandHandler(
            _reporting,
            _fxRates,
            _unitOfWork,
            NullLogger<ProjectReportingEntriesCommandHandler>.Instance);
    }

    private void Pending(params LedgerTransaction[] transactions) =>
        _reporting.GetUnprojectedTransactionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(transactions.ToList());

    private static LedgerTransaction Capture(string currency, decimal amount = 100m) =>
        LedgerTransaction.ForCapture(
            Guid.NewGuid(), Guid.NewGuid(), $"pay-{Guid.NewGuid():N}", new Money(amount, currency), 0m, 0m, OccurredAt);

    private Task<Application.Common.Result<ReportingProjectionReport>> RunAsync() =>
        _handler.Handle(new ProjectReportingEntriesCommand("USD", 200), CancellationToken.None);

    [Fact]
    public async Task Handle_ShouldUseTheIdentityRate_WithoutLookingUpAnFxRate()
    {
        Pending(Capture("USD"));

        var result = await RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.TransactionsProjected);
        Assert.All(_written, e => Assert.Equal(1m, e.RateUsed));

        await _fxRates.DidNotReceiveWithAnyArgs().GetEffectiveRateAsync(
            default!, default!, default, default);
    }

    [Fact]
    public async Task Handle_ShouldConvertAtTheRateEffectiveWhenTheTransactionOccurred()
    {
        var transaction = Capture("EUR");
        Pending(transaction);

        _fxRates.GetEffectiveRateAsync("EUR", "USD", transaction.OccurredAt, Arg.Any<CancellationToken>())
            .Returns(FxRate.Create("EUR", "USD", 1.1m, OccurredAt.AddDays(-1)));

        var result = await RunAsync();

        Assert.Equal(1, result.Value!.TransactionsProjected);
        Assert.All(_written, e => Assert.Equal(110m, e.ReportingAmount));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldLeaveATransactionUnprojected_WhenNoRateExists()
    {
        // Booking it at a guessed rate would be worse than leaving it queued: the transaction
        // stays pending and converts correctly once the rate lands.
        Pending(Capture("GBP"));

        _fxRates.GetEffectiveRateAsync("GBP", "USD", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((FxRate?)null);

        var result = await RunAsync();

        Assert.Equal(0, result.Value!.TransactionsProjected);
        Assert.Equal("GBP->USD", Assert.Single(result.Value.MissingRateCurrencies));
        Assert.Empty(_written);

        await _reporting.DidNotReceiveWithAnyArgs().AddRangeAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Handle_ShouldStillProjectTheOthers_WhenOneCurrencyHasNoRate()
    {
        Pending(Capture("USD"), Capture("GBP"));

        _fxRates.GetEffectiveRateAsync("GBP", "USD", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((FxRate?)null);

        var result = await RunAsync();

        Assert.Equal(1, result.Value!.TransactionsProjected);
        Assert.Single(result.Value.MissingRateCurrencies);
    }

    [Fact]
    public async Task Handle_ShouldDoNothing_WhenNothingIsPending()
    {
        Pending();

        var result = await RunAsync();

        Assert.Equal(0, result.Value!.TransactionsProjected);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Handle_ShouldRejectAnEmptyReportingCurrency()
    {
        var result = await _handler.Handle(
            new ProjectReportingEntriesCommand("  ", 200), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_ShouldWriteBalancedReportingEntries()
    {
        var transaction = LedgerTransaction.ForCapture(
            Guid.NewGuid(), null, "pay-1", new Money(100m, "EUR"), fee: 3m, tax: 20m, OccurredAt);
        Pending(transaction);

        _fxRates.GetEffectiveRateAsync("EUR", "USD", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(FxRate.Create("EUR", "USD", 1.09m, OccurredAt.AddDays(-1)));

        await RunAsync();

        var debits = _written.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.ReportingAmount);
        var credits = _written.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.ReportingAmount);

        Assert.Equal(debits, credits);
        Assert.Equal(109m, debits);
    }
}
