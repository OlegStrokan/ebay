using Application.Commands.ReconcileLedger;
using Application.Common.Enums;
using Application.Gateways;
using Application.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests;

public class ReconcileLedgerCommandHandlerTests
{
    private readonly ILedgerReconciliationReader _reader = Substitute.For<ILedgerReconciliationReader>();
    private readonly IMoneyEventConsumerMonitor _monitor = Substitute.For<IMoneyEventConsumerMonitor>();
    private readonly IIncidentReporter _reporter = Substitute.For<IIncidentReporter>();
    private readonly ReconcileLedgerCommandHandler _handler;

    public ReconcileLedgerCommandHandlerTests()
    {
        _reader.GetCurrencyBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyBalance> { new("EUR", 500m, 500m) });
        _reader.GetUnbalancedTransactionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<UnbalancedTransaction>());
        _reader.GetOverRefundedOrdersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OverRefundedOrder>());
        _monitor.TakeSnapshot().Returns(new MoneyEventConsumerSnapshot(0, 0, [], DateTime.UtcNow));

        _handler = new ReconcileLedgerCommandHandler(
            _reader,
            _monitor,
            _reporter,
            NullLogger<ReconcileLedgerCommandHandler>.Instance);
    }

    private Task<Application.Common.Result<LedgerReconciliationReport>> RunAsync() =>
        _handler.Handle(new ReconcileLedgerCommand(20, 100), CancellationToken.None);

    [Fact]
    public async Task Handle_ShouldReportHealthy_AndNotPage_WhenEverythingBalances()
    {
        var result = await RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsHealthy);
        Assert.Empty(result.Value.Findings);

        await _reporter.DidNotReceive().ReportAsync(Arg.Any<LedgerIncident>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldFlagDoubleRefund_AndPage()
    {
        // The post-mortem case: a compensation retry refunded twice. Each posting is internally
        // balanced, so Sigma-debits = Sigma-credits still holds and only this per-order comparison
        // catches it.
        var orderId = Guid.NewGuid();
        _reader.GetOverRefundedOrdersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OverRefundedOrder> { new(orderId, "USD", Captured: 4180m, Refunded: 8360m) });

        var result = await RunAsync();

        Assert.False(result.Value!.IsHealthy);
        var finding = Assert.Single(result.Value.Findings);
        Assert.Contains(orderId.ToString(), finding);
        Assert.Contains("over-refunded by 4180", finding);

        await _reporter.Received(1).ReportAsync(
            Arg.Is<LedgerIncident>(i => i.Severity == AlertSeverity.Critical && i.Details.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldFlagCurrencyDrift()
    {
        _reader.GetCurrencyBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyBalance> { new("EUR", 500m, 450m) });

        var result = await RunAsync();

        Assert.False(result.Value!.IsHealthy);
        Assert.Contains("drift 50", Assert.Single(result.Value.Findings));
        await _reporter.Received(1).ReportAsync(Arg.Any<LedgerIncident>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldFlagUnbalancedTransaction()
    {
        _reader.GetUnbalancedTransactionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<UnbalancedTransaction>
            {
                new(Guid.NewGuid(), "capture:pay-1", "EUR", 100m, 90m),
            });

        var result = await RunAsync();

        Assert.False(result.Value!.IsHealthy);
        Assert.Contains("capture:pay-1", Assert.Single(result.Value.Findings));
    }

    [Fact]
    public async Task Handle_ShouldFlagEventsTheConsumerCommittedPast()
    {
        // The consumer deliberately abandons poison messages. Without this, that drift is invisible.
        _monitor.TakeSnapshot().Returns(
            new MoneyEventConsumerSnapshot(0, 3, ["pay-1:captured"], DateTime.UtcNow));

        var result = await RunAsync();

        Assert.False(result.Value!.IsHealthy);
        Assert.Contains("pay-1:captured", Assert.Single(result.Value.Findings));
    }

    [Fact]
    public async Task Handle_ShouldFlagConsumerLag_WhenAboveThreshold()
    {
        _monitor.TakeSnapshot().Returns(new MoneyEventConsumerSnapshot(5_000, 0, [], DateTime.UtcNow));

        var result = await RunAsync();

        Assert.False(result.Value!.IsHealthy);
        Assert.Contains("lag is 5000", Assert.Single(result.Value.Findings));
    }

    [Fact]
    public async Task Handle_ShouldNotFlagLag_WhenThresholdIsDisabled()
    {
        _monitor.TakeSnapshot().Returns(new MoneyEventConsumerSnapshot(5_000, 0, [], DateTime.UtcNow));

        var result = await _handler.Handle(new ReconcileLedgerCommand(20, 0), CancellationToken.None);

        Assert.True(result.Value!.IsHealthy);
    }
}
