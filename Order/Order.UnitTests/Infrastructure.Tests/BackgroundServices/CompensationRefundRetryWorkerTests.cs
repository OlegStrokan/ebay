using Application.Common.Enums;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Models;
using Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Infrastructure.Tests.BackgroundServices;

public class CompensationRefundRetryWorkerTests
{
    private readonly ICompensationRefundRetryRepository _retryRepository =
        Substitute.For<ICompensationRefundRetryRepository>();

    private readonly IPaymentGateway _paymentGateway = Substitute.For<IPaymentGateway>();
    private readonly IIncidentReporter _incidentReporter = Substitute.For<IIncidentReporter>();
    private readonly ILogger<CompensationRefundRetryWorker> _logger =
        Substitute.For<ILogger<CompensationRefundRetryWorker>>();

    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IServiceScope _serviceScope = Substitute.For<IServiceScope>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    public CompensationRefundRetryWorkerTests()
    {
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(_scopeFactory);
        _scopeFactory.CreateScope().Returns(_serviceScope);
        _serviceScope.ServiceProvider.GetService(typeof(ICompensationRefundRetryRepository)).Returns(_retryRepository);
        _serviceScope.ServiceProvider.GetService(typeof(IPaymentGateway)).Returns(_paymentGateway);
        _serviceScope.ServiceProvider.GetService(typeof(IIncidentReporter)).Returns(_incidentReporter);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkCompleted_WhenRefundSucceeds()
    {
        var retry = CreateRetry();
        ConfigureSingleDueRetry(retry);

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new RefundProcessingResult("REF-1", RefundProcessingStatus.Succeeded));

        var signal = new TaskCompletionSource<bool>();
        _retryRepository
            .When(x => x.SaveAsync(Arg.Any<CompensationRefundRetry>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = BuildWorker();
        await RunUntilSignalAsync(worker, signal, "SaveAsync was not called for successful refund retry.");

        Assert.Equal(CompensationRefundRetryStatus.Completed, retry.Status);
        Assert.NotNull(retry.CompletedAtUtc);
        await _incidentReporter.DidNotReceive().SendAlertAsync(Arg.Any<IncidentAlert>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepPendingAndScheduleVerification_WhenRefundIsPending()
    {
        var retry = CreateRetry();
        var before = retry.NextAttemptAtUtc;
        ConfigureSingleDueRetry(retry);

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new RefundProcessingResult("REF-PENDING", RefundProcessingStatus.Pending));

        var signal = new TaskCompletionSource<bool>();
        _retryRepository
            .When(x => x.SaveAsync(Arg.Any<CompensationRefundRetry>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = BuildWorker();
        await RunUntilSignalAsync(worker, signal, "SaveAsync was not called for pending refund retry.");

        Assert.Equal(CompensationRefundRetryStatus.Pending, retry.Status);
        Assert.Null(retry.CompletedAtUtc);
        Assert.Equal(0, retry.RetryCount);
        Assert.True(retry.NextAttemptAtUtc > before);
        await _incidentReporter.DidNotReceive().SendAlertAsync(Arg.Any<IncidentAlert>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldScheduleNextAttempt_WhenFailureIsRetriable()
    {
        var retry = CreateRetry();
        var before = retry.NextAttemptAtUtc;
        ConfigureSingleDueRetry(retry);

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Throws(new GatewayUnavailableException(GatewayUnavailableReason.Timeout, "timeout"));

        var signal = new TaskCompletionSource<bool>();
        _retryRepository
            .When(x => x.SaveAsync(Arg.Any<CompensationRefundRetry>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = BuildWorker(maxRetries: 3);
        await RunUntilSignalAsync(worker, signal, "SaveAsync was not called for retriable retry failure.");

        Assert.Equal(CompensationRefundRetryStatus.Pending, retry.Status);
        Assert.Equal(1, retry.RetryCount);
        Assert.Equal("timeout", retry.LastError);
        Assert.True(retry.NextAttemptAtUtc > before);

        await _incidentReporter.DidNotReceive().SendAlertAsync(Arg.Any<IncidentAlert>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkExhaustedAndAlert_WhenRetriableFailureHitsLimit()
    {
        var retry = CreateRetry();
        var now = DateTime.UtcNow;
        retry.MarkAttemptFailed("first", now.AddMinutes(1), now);
        retry.MarkAttemptFailed("second", now.AddMinutes(2), now.AddSeconds(10));
        ConfigureSingleDueRetry(retry);

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Throws(new GatewayUnavailableException(GatewayUnavailableReason.Timeout, "still failing"));

        var signal = new TaskCompletionSource<bool>();
        _incidentReporter
            .When(x => x.SendAlertAsync(Arg.Any<IncidentAlert>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = BuildWorker(maxRetries: 3);
        await RunUntilSignalAsync(worker, signal, "Incident alert was not sent after retry exhaustion.");

        Assert.Equal(CompensationRefundRetryStatus.Exhausted, retry.Status);
        Assert.Equal(3, retry.RetryCount);

        await _incidentReporter.Received(1).SendAlertAsync(
            Arg.Is<IncidentAlert>(a =>
                a.AlertType == "PaymentRefundCompensationRetryExhausted"
                && a.OrderId == retry.OrderId
                && a.Severity == AlertSeverity.Critical),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkExhaustedAndAlert_WhenFailureIsNonRetriable()
    {
        var retry = CreateRetry();
        ConfigureSingleDueRetry(retry);

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("non transient"));

        var signal = new TaskCompletionSource<bool>();
        _incidentReporter
            .When(x => x.SendAlertAsync(Arg.Any<IncidentAlert>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = BuildWorker(maxRetries: 3);
        await RunUntilSignalAsync(worker, signal, "Incident alert was not sent for non-retriable failure.");

        Assert.Equal(CompensationRefundRetryStatus.Exhausted, retry.Status);
        Assert.Equal(1, retry.RetryCount);
        Assert.Equal("non transient", retry.LastError);

        await _incidentReporter.Received(1).SendAlertAsync(
            Arg.Is<IncidentAlert>(a => a.AlertType == "PaymentRefundCompensationRetryExhausted"),
            Arg.Any<CancellationToken>());
    }

    private CompensationRefundRetryWorker BuildWorker(int maxRetries = 3)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompensationRefundRetry:BatchSize"] = "20",
                ["CompensationRefundRetry:MaxRetries"] = maxRetries.ToString(),
                ["CompensationRefundRetry:PollIntervalSeconds"] = "60",
                ["CompensationRefundRetry:BaseRetryDelaySeconds"] = "10",
                ["CompensationRefundRetry:MaxRetryDelaySeconds"] = "120"
            })
            .Build();

        return new CompensationRefundRetryWorker(_serviceProvider, _logger, configuration);
    }

    private void ConfigureSingleDueRetry(CompensationRefundRetry retry)
    {
        var callCount = 0;
        _retryRepository.ClaimDuePendingAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? (IReadOnlyList<CompensationRefundRetry>)new List<CompensationRefundRetry> { retry }
                    : Array.Empty<CompensationRefundRetry>();
            });
    }

    private static CompensationRefundRetry CreateRetry()
    {
        return CompensationRefundRetry.Create(
            Guid.NewGuid(),
            "PAY-123",
            25m,
            "USD",
            "Order cancelled - saga compensation",
            DateTime.UtcNow.AddMinutes(-1));
    }

    private static async Task RunUntilSignalAsync(
        CompensationRefundRetryWorker worker,
        TaskCompletionSource<bool> signal,
        string failureMessage)
    {
        await worker.StartAsync(CancellationToken.None);

        var completedTask = await Task.WhenAny(signal.Task, Task.Delay(3000));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(completedTask == signal.Task, failureMessage);
    }
}
