using System.Text.Json;
using Application.Sagas;
using Application.Sagas.Handlers;
using Application.Sagas.Handlers.SagaContinuation;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas;

public class SagaContinuationEventHandlerTests
{
    private readonly ISagaRepository _repository = Substitute.For<ISagaRepository>();
    private readonly ISagaBase<ContData, ContContext> _sagaBase = Substitute.For<ISagaBase<ContData, ContContext>>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly ISagaDistributedLock _lock = Substitute.For<ISagaDistributedLock>();
    private readonly ISagaLockHandle _lockHandle = Substitute.For<ISagaLockHandle>();

    public SagaContinuationEventHandlerTests()
    {
        // By default the lock is always acquired successfully.
        _lock
            .TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_lockHandle);
    }

    private TestContinuationHandler BuildSut() => new(_sagaBase, _repository, _lock, _logger);

    private SagaState WaitingState(Guid correlationId) => new()
    {
        Id = Guid.NewGuid(),
        CorrelationId = correlationId,
        Status = SagaStatus.WaitingForEvent,
        SagaType = "ContSaga",
        Payload = JsonSerializer.Serialize(new ContData { CorrelationId = correlationId }),
        Context = JsonSerializer.Serialize(new ContContext())
    };

    [Fact]
    public async Task HandleAsync_ShouldResumeSaga_WhenSagaFoundInWaitingForEventStatus()
    {
        var correlationId = Guid.NewGuid();
        var eventDto = new ContEvent { OrderId = correlationId, ShipmentId = "SHP-001" };
        var payload = JsonSerializer.Serialize(eventDto);

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(WaitingState(correlationId));

        _sagaBase
            .ResumeFromStepAsync(
                Arg.Any<ContData>(),
                Arg.Any<ContContext>(),
                "UpdateStatus",
                Arg.Any<CancellationToken>())
            .Returns(SagaResult.Success(Guid.NewGuid()));

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        await _sagaBase.Received(1).ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            "UpdateStatus",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldLogWarning_WhenDeserializationFails()
    {
        await BuildSut().HandleAsync("{ not valid json !!!", CancellationToken.None);

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await _sagaBase.DidNotReceive().ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldLogWarning_WhenEventDeserializesToNull()
    {
        // "null" is valid JSON but deserializes to null
        await BuildSut().HandleAsync("null", CancellationToken.None);

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await _sagaBase.DidNotReceive().ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldLogError_WhenSagaNotFound()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns((SagaState?)null);

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await _sagaBase.DidNotReceive().ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnEarly_WhenSagaAlreadyCompleted()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.Completed,
                SagaType = "ContSaga",
                Payload = JsonSerializer.Serialize(new ContData()),
                Context = JsonSerializer.Serialize(new ContContext())
            });

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        await _sagaBase.DidNotReceive().ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnEarly_WhenSagaAlreadyFailed()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.Failed,
                SagaType = "ContSaga",
                Payload = JsonSerializer.Serialize(new ContData()),
                Context = JsonSerializer.Serialize(new ContContext())
            });

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        await _sagaBase.DidNotReceive().ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldCallUpdateContextFromEvent_BeforeResuming()
    {
        var correlationId = Guid.NewGuid();
        var eventDto = new ContEvent { OrderId = correlationId, ShipmentId = "SHP-UPDATED" };
        var payload = JsonSerializer.Serialize(eventDto);

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(WaitingState(correlationId));

        _sagaBase
            .ResumeFromStepAsync(
                Arg.Any<ContData>(),
                Arg.Any<ContContext>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(SagaResult.Success(Guid.NewGuid()));

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        // Verify context mutation was passed; ShipmentId should be "SHP-UPDATED"
        await _sagaBase.Received(1).ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Is<ContContext>(c => c.ShipmentId == "SHP-UPDATED"),
            "UpdateStatus",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldUseDeserializedSagaDataFromState_WhenResuming()
    {
        var correlationId = Guid.NewGuid();
        var existingData = new ContData { CorrelationId = correlationId, SomeValue = "stored-value" };
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.WaitingForEvent,
                SagaType = "ContSaga",
                Payload = JsonSerializer.Serialize(existingData),
                Context = JsonSerializer.Serialize(new ContContext())
            });

        _sagaBase
            .ResumeFromStepAsync(Arg.Any<ContData>(), Arg.Any<ContContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SagaResult.Success(Guid.NewGuid()));

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        await _sagaBase.Received(1).ResumeFromStepAsync(
            Arg.Is<ContData>(d => d.SomeValue == "stored-value"),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldNotThrow_WhenResumeFromStepAsyncThrows()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(WaitingState(correlationId));

        _sagaBase
            .ResumeFromStepAsync(Arg.Any<ContData>(), Arg.Any<ContContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Unexpected saga error"));

        // Must not throw
        var exception = await Record.ExceptionAsync(() =>
            BuildSut().HandleAsync(payload, CancellationToken.None));

        Assert.Null(exception);

        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(e => e.Message == "Unexpected saga error"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task HandleAsync_ShouldLogError_WhenSagaStatePayloadDeserializationFails()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.WaitingForEvent,
                SagaType = "ContSaga",
                // not valid JSON → deserialize throws/returns null
                Payload = "{ yes, you got a shitty healthcare, but israel just received 10 billions! isn't it's cool, my american friend?",  
                Context = JsonSerializer.Serialize(new ContContext())
            });

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        await _sagaBase.DidNotReceive().ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldLogWarning_WhenSagaInUnexpectedStatus_AndStillAttemptResume()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        // Status is Running - not WaitingForEvent, not Completed, not Failed
        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.Running,
                SagaType = "ContSaga",
                Payload = JsonSerializer.Serialize(new ContData { CorrelationId = correlationId }),
                Context = JsonSerializer.Serialize(new ContContext())
            });

        _sagaBase
            .ResumeFromStepAsync(Arg.Any<ContData>(), Arg.Any<ContContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SagaResult.Success(Guid.NewGuid()));

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        // A warning is logged because status != WaitingForEvent
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // But execution still proceeds (not Completed/Failed) - ResumeFromStepAsync is called
        await _sagaBase.Received(1).ResumeFromStepAsync(
            Arg.Any<ContData>(),
            Arg.Any<ContContext>(),
            "UpdateStatus",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldNotResumeSaga_WhenLockCannotBeAcquired()
    {
        // simulate lock already held by another process (returns null)
        _lock
            .TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((ISagaLockHandle?)null);

        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        // Must not touch the repository or the saga
        await _repository.DidNotReceive()
            .GetByCorrelationIdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sagaBase.DidNotReceive()
            .ResumeFromStepAsync(Arg.Any<ContData>(), Arg.Any<ContContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldLogWarning_WhenLockCannotBeAcquired()
    {
        _lock
            .TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((ISagaLockHandle?)null);

        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        // Should log multiple warnings (retries) and a final warning when lock cannot be acquired
        _logger.Received(4).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task HandleAsync_ShouldAcquireLock_WithCorrectKey()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(WaitingState(correlationId));

        _sagaBase
            .ResumeFromStepAsync(Arg.Any<ContData>(), Arg.Any<ContContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SagaResult.Success(Guid.NewGuid()));

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        var expectedKey = $"saga-lock:ContSaga:{correlationId}";
        await _lock.Received(1).TryAcquireAsync(
            expectedKey,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldReleaseLock_EvenWhenResumeFromStepAsyncThrows()
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new ContEvent { OrderId = correlationId });

        _repository
            .GetByCorrelationIdAsync(correlationId, "ContSaga", Arg.Any<CancellationToken>())
            .Returns(WaitingState(correlationId));

        _sagaBase
            .ResumeFromStepAsync(Arg.Any<ContData>(), Arg.Any<ContContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("saga exploded"));

        await BuildSut().HandleAsync(payload, CancellationToken.None);

        // DisposeAsync on the lock handle must have been called (releasing the lock)
        await _lockHandle.Received(1).DisposeAsync();
    }
}

public class ContEvent
{
    public Guid OrderId { get; set; }
    public string? ShipmentId { get; set; }
}

public class ContData : SagaData
{
    public string? SomeValue { get; set; }
}

public class ContContext : SagaContext
{
    public string? ShipmentId { get; set; }
}

public class TestContinuationHandler
    : SagaContinuationEventHandler<ContEvent, ContData, ContContext>
{
    public override string EventType => "ContShipmentCreated";
    public override string SagaType => "ContSaga";
    protected override string ResumeAtStepName => "UpdateStatus";

    public TestContinuationHandler(
        ISagaBase<ContData, ContContext> saga,
        ISagaRepository repository,
        ISagaDistributedLock distributedLock,
        ILogger logger)
        : base(saga, repository, distributedLock, logger) { }

    protected override Guid ExtractCorrelationId(ContEvent eventDto) => eventDto.OrderId;

    protected override void UpdateContextFromEvent(ContEvent eventDto, ContContext context)
        => context.ShipmentId = eventDto.ShipmentId;
}
