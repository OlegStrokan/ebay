using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Application.Sagas.ReturnSaga;
using Infrastructure.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Infrastructure.Tests.BackgroundServices;

public class SagaWatchdogServiceTests
{
    private readonly ISagaRepository _sagaRepository = Substitute.For<ISagaRepository>();
    private readonly IOrderSaga _orderSaga = Substitute.For<IOrderSaga>();
    private readonly IReturnSaga _returnSaga = Substitute.For<IReturnSaga>();
    private readonly ILogger<SagaWatchdogService> _logger = Substitute.For<ILogger<SagaWatchdogService>>();

    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IServiceScope _serviceScope = Substitute.For<IServiceScope>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly ISagaDistributedLock _distributedLock = Substitute.For<ISagaDistributedLock>();
    private readonly ISagaLockHandle _lockHandle = Substitute.For<ISagaLockHandle>();

    public SagaWatchdogServiceTests()
    {
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(_scopeFactory);
        _scopeFactory.CreateScope().Returns(_serviceScope);
        _serviceScope.ServiceProvider.GetService(typeof(ISagaRepository)).Returns(_sagaRepository);
        _serviceScope.ServiceProvider.GetService(typeof(IOrderSaga)).Returns(_orderSaga);
        _serviceScope.ServiceProvider.GetService(typeof(IReturnSaga)).Returns(_returnSaga);
        _serviceScope.ServiceProvider.GetService(typeof(ISagaDistributedLock)).Returns(_distributedLock);

        // Compensation now runs under the saga distributed lock; grant it by default.
        _distributedLock
            .TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_lockHandle);
    }

    private SagaWatchdogService Build() => new(_serviceProvider, _logger);
    
    [Fact]
    public async Task ExecuteAsync_ShouldMarkAsCompleted_WhenAllStepsAreCompleted()
    {
        var sagaId = Guid.NewGuid();
        var saga = new SagaState
        {
            Id       = sagaId,
            SagaType = "OrderSaga",
            Status   = SagaStatus.Completed,
            // UpdatedAt can be recent - stuck detection is done by the repository; we
            // just care about the in-service logic after a saga is returned as "stuck"
            UpdatedAt = DateTime.UtcNow.AddMinutes(-3),
            Steps     = new List<SagaStepLog>()
        };

        _sagaRepository
            .GetStuckSagasAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<SagaState> { saga });

        _sagaRepository
            .GetStepLogsAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new List<SagaStepLog>
            {
                new() { Id = Guid.NewGuid(), Status = StepStatus.Completed },
                new() { Id = Guid.NewGuid(), Status = StepStatus.Completed }
            });

        using var cts = new CancellationTokenSource();
        var signal = new TaskCompletionSource<bool>();

        _sagaRepository
            .When(r => r.SaveAsync(
                Arg.Is<SagaState>(s => s.Status == SagaStatus.Completed),
                Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                signal.TrySetResult(true);
                cts.Cancel();
            });

        await Build().StartAsync(cts.Token);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(3000));
        Assert.True(completed == signal.Task, "SaveAsync(Completed) was never called within 3 seconds.");

        await _sagaRepository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Status == SagaStatus.Completed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailAndCompensateOrderSaga_WhenStuckBeyondTwiceThreshold()
    {
        var sagaId = Guid.NewGuid();
        var saga = new SagaState
        {
            Id        = sagaId,
            SagaType  = "OrderSaga",
            Status    = SagaStatus.Running,
            // 11 minutes ago - well beyond 2× the 5-minute _stuckThreshold
            UpdatedAt = DateTime.UtcNow.AddMinutes(-11),
            Steps     = new List<SagaStepLog>()
        };

        _sagaRepository
            .GetStuckSagasAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<SagaState> { saga });

        // Not all steps completed → IsSagaActuallyCompletedAsync returns false
        _sagaRepository
            .GetStepLogsAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new List<SagaStepLog>
            {
                new() { Status = StepStatus.Failed }
            });

        // Compensation re-reads the saga under the lock
        _sagaRepository
            .GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(saga);

        using var cts = new CancellationTokenSource();
        var signal = new TaskCompletionSource<bool>();

        _orderSaga
            .When(s => s.CompensateAsync(sagaId, Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                signal.TrySetResult(true);
                cts.Cancel();
            });

        await Build().StartAsync(cts.Token);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(3000));
        Assert.True(completed == signal.Task, "OrderSaga.CompensateAsync was never called within 3 seconds.");

        await _orderSaga.Received().CompensateAsync(sagaId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailAndCompensateReturnSaga_WhenStuckBeyondTwiceThreshold()
    {
        var sagaId = Guid.NewGuid();
        var saga = new SagaState
        {
            Id        = sagaId,
            SagaType  = "ReturnSaga",
            Status    = SagaStatus.Running,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-11),
            Steps     = new List<SagaStepLog>()
        };

        _sagaRepository
            .GetStuckSagasAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<SagaState> { saga });

        _sagaRepository
            .GetStepLogsAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new List<SagaStepLog>());

        _sagaRepository
            .GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(saga);

        using var cts = new CancellationTokenSource();
        var signal = new TaskCompletionSource<bool>();

        _returnSaga
            .When(s => s.CompensateAsync(sagaId, Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                signal.TrySetResult(true);
                cts.Cancel();
            });

        await Build().StartAsync(cts.Token);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(3000));
        Assert.True(completed == signal.Task, "ReturnSaga.CompensateAsync was never called within 3 seconds.");

        await _returnSaga.Received().CompensateAsync(sagaId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCompensateImmediately_WhenSagaIsTimedOut()
    {
        // TimedOut sagas must skip the tolerance window entirely and be compensated
        // on the first watchdog poll, even if UpdatedAt is recent (within the 2x threshold)
        // covers the crash-recovery case: SagaBase saved TimedOut but the process
        // died before CompensateAsync could finish.
        var sagaId = Guid.NewGuid();
        var saga = new SagaState
        {
            Id        = sagaId,
            SagaType  = "OrderSaga",
            Status    = SagaStatus.TimedOut,
            // Only 3 minutes ago - inside the 2× 5-min tolerance window
            // A Running saga this recent would NOT be compensated yet
            // A TimedOut saga must be compensated immediately regardless
            UpdatedAt = DateTime.UtcNow.AddMinutes(-3),
            Steps     = new List<SagaStepLog>()
        };

        _sagaRepository
            .GetStuckSagasAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<SagaState> { saga });

        _sagaRepository
            .GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(saga);

        using var cts = new CancellationTokenSource();
        var signal = new TaskCompletionSource<bool>();

        _orderSaga
            .When(s => s.CompensateAsync(sagaId, Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                signal.TrySetResult(true);
                cts.Cancel();
            });

        await Build().StartAsync(cts.Token);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(3000));
        Assert.True(completed == signal.Task, "OrderSaga.CompensateAsync was never called within 3 seconds.");

        await _orderSaga.Received().CompensateAsync(sagaId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotCompensate_WhenNoStuckSagasFound()
    {
        _sagaRepository
            .GetStuckSagasAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<SagaState>());

        using var cts = new CancellationTokenSource();
        var signal = new TaskCompletionSource<bool>();

        // signal after GetStuckSagasAsync so we know the check ran, then cancel
        _sagaRepository
            .When(r => r.GetStuckSagasAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                signal.TrySetResult(true);
                cts.Cancel();
            });

        await Build().StartAsync(cts.Token);

        var completed = await Task.WhenAny(signal.Task, Task.Delay(3000));
        Assert.True(completed == signal.Task);

        await _orderSaga.DidNotReceive().CompensateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _returnSaga.DidNotReceive().CompensateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
