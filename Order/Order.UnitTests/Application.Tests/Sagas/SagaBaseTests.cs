using System.Text.Json;
using Application.Interfaces;
using Application.Sagas;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas;

public class SagaBaseTests
{
    private readonly ISagaRepository _repository = Substitute.For<ISagaRepository>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly ISagaErrorClassifier _errorClassifier = Substitute.For<ISagaErrorClassifier>();
    private readonly IFailedCompensationRetryRepository _failedCompensationRetryRepository =
        Substitute.For<IFailedCompensationRetryRepository>();

    private readonly ISagaStep<TestSagaData, TestSagaContext> _step1 =
        Substitute.For<ISagaStep<TestSagaData, TestSagaContext>>();
    private readonly ISagaStep<TestSagaData, TestSagaContext> _step2 =
        Substitute.For<ISagaStep<TestSagaData, TestSagaContext>>();
    private readonly ISagaStep<TestSagaData, TestSagaContext> _step3 =
        Substitute.For<ISagaStep<TestSagaData, TestSagaContext>>();

    public class TestSaga : SagaBase<TestSagaData, TestSagaContext>
    {
        public TestSaga(
            ISagaRepository repo,
            IEnumerable<ISagaStep<TestSagaData, TestSagaContext>> steps,
            ISagaErrorClassifier errorClassifier,
            ILogger logger,
            IFailedCompensationRetryRepository failedCompensationRetryRepository)
            : base(repo, steps, errorClassifier, logger, failedCompensationRetryRepository) {}

        protected override string SagaType => "TestSaga";
    }

    // Override SagaTimeout to make timeout tests fast (no 5-min wait)
    public class TestSagaWithShortTimeout : SagaBase<TestSagaData, TestSagaContext>
    {
        public TestSagaWithShortTimeout(
            ISagaRepository repo,
            IEnumerable<ISagaStep<TestSagaData, TestSagaContext>> steps,
            ISagaErrorClassifier errorClassifier,
            ILogger logger,
            IFailedCompensationRetryRepository failedCompensationRetryRepository)
            : base(repo, steps, errorClassifier, logger, failedCompensationRetryRepository) {}

        protected override string SagaType => "TestSagaWithShortTimeout";
        protected override TimeSpan SagaTimeout => TimeSpan.FromMilliseconds(100);
    }

    public class TestSagaData : SagaData {}
    public class TestSagaContext : SagaContext {}

    private TestSaga Build(params ISagaStep<TestSagaData, TestSagaContext>[] steps)
        => new(_repository, steps, _errorClassifier, _logger, _failedCompensationRetryRepository);

    [Fact]
    public async Task ExecuteAsync_ShouldRunAllSteps_AndComplete_WhenAllStepsSucceed()
    {
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Completed());

        _step2.Order.Returns(2);
        _step2.StepName.Returns("Step2");
        _step2.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Completed());

        var result = await Build(_step1, _step2)
            .ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SagaStatus.Completed, result.Status);

        await _step1.Received(1).ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
        await _step2.Received(1).ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());

        // final save must be Completed
        await _repository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Status == SagaStatus.Completed && s.CurrentStep == "Step2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPauseSaga_WhenStepReturnsWaitingForEventMetadata()
    {
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new WaitForEvent());
        

        _step2.Order.Returns(2);
        _step2.StepName.Returns("Step2");

        // Mock both SaveAsync and GetByIdAsync
        _repository.SaveAsync(Arg.Any<SagaState>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(args => new SagaState
            {
                Id = (Guid)args[0],
                Status = SagaStatus.Running,
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext()),
                Steps = new List<SagaStepLog>()
            });

        var result = await Build(_step1, _step2)
            .ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, CancellationToken.None);

        // SagaResult.Success always sets status=completed (it signals the handler "we are done for now type shit")
        Assert.True(result.IsSuccess);
        Assert.Equal(SagaStatus.Completed, result.Status);

        // saga state persisted as WaitingForEvent after Step1 completes
        await _repository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Status == SagaStatus.WaitingForEvent && s.CurrentStep == "Step1"),
            Arg.Any<CancellationToken>());

        // step2 must never run
        await _step2.DidNotReceive().ExecuteAsync(
            Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCompensate_WhenStepFails()
    {
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Completed());

        _step2.Order.Returns(2);
        _step2.StepName.Returns("Step2");
        _step2.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Fail("Step2 failed"));

        // compensateAsync loads saga state by the internal sagaId via GetByIdAsync
        // we need Steps to contain Step1 as Completed so compensation runs it
        _repository
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(args => new SagaState
            {
                Id = (Guid)args[0],
                Status = SagaStatus.Failed,
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext()),
                Steps = new List<SagaStepLog>
                {
                    new() { StepName = "Step1", Status = StepStatus.Completed }
                }
            });

        var result = await Build(_step1, _step2)
            .ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SagaStatus.Failed, result.Status);
        Assert.Equal("Step2 failed", result.ErrorMessage);

        // Step1 was completed → must be compensated
        await _step1.Received(1).CompensateAsync(
            Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());

        // Step2 failed → must NOT be compensated
        await _step2.DidNotReceive().CompensateAsync(
            Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSaveProgress_AfterEachStep()
    {
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Completed());

        _step2.Order.Returns(2);
        _step2.StepName.Returns("Step2");
        _step2.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Completed());

        // Capture the state of each SaveAsync call at the time it's made
        var capturedStates = new List<(string? CurrentStep, SagaStatus Status)>();
        _repository
            .SaveAsync(Arg.Do<SagaState>(s => capturedStates.Add((s.CurrentStep, s.Status))), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await Build(_step1, _step2)
            .ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, CancellationToken.None);

        // Verify SaveAsync was called exactly 4 times
        Assert.Equal(4, capturedStates.Count);

        // First save: initial saga creation (CurrentStep should be empty string or null, Status = Running)
        Assert.True(string.IsNullOrEmpty(capturedStates[0].CurrentStep), $"Expected null or empty at index 0, got '{capturedStates[0].CurrentStep}'");
        Assert.Equal(SagaStatus.Running, capturedStates[0].Status);

        // Second save: after Step1 completes
        Assert.Equal("Step1", capturedStates[1].CurrentStep);
        Assert.Equal(SagaStatus.Running, capturedStates[1].Status);

        // Third save: after Step2 completes
        Assert.Equal("Step2", capturedStates[2].CurrentStep);
        Assert.Equal(SagaStatus.Running, capturedStates[2].Status);

        // Fourth save: final completed state - CurrentStep remains Step2 (not changed)
        Assert.Equal("Step2", capturedStates[3].CurrentStep);
        Assert.Equal(SagaStatus.Completed, capturedStates[3].Status);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryTransientException_ThenSucceed()
    {
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");

        var callCount = 0;
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 2) throw new Exception("transient");
                return Task.FromResult<StepOutcome>(new Completed());
            });

        // classifier says the exception is transient
        _errorClassifier.IsTransient(Arg.Any<Exception>()).Returns(true);

        var result = await Build(_step1)
            .ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ResumeFromStepAsync_ShouldResumeFromSpecificStep_SkippingEarlierSteps()
    {
        var correlationId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        _step1.Order.Returns(1); _step1.StepName.Returns("Step1");
        _step2.Order.Returns(2); _step2.StepName.Returns("Step2");
        _step3.Order.Returns(3); _step3.StepName.Returns("Step3");
        _step3.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(new Completed());

        _repository.GetByCorrelationIdAsync(correlationId, "TestSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                CorrelationId = correlationId,
                Status = SagaStatus.WaitingForEvent,
                SagaType = "TestSaga",
                Payload = JsonSerializer.Serialize(new TestSagaData { CorrelationId = correlationId }),
                Context = JsonSerializer.Serialize(new TestSagaContext())
            });

        var result = await Build(_step1, _step2, _step3)
            .ResumeFromStepAsync(new TestSagaData { CorrelationId = correlationId }, new TestSagaContext(), "Step3", CancellationToken.None);

        Assert.True(result.IsSuccess);

        await _step1.DidNotReceive().ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
        await _step2.DidNotReceive().ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
        await _step3.Received(1).ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeFromStepAsync_ShouldFail_WhenSagaNotFound()
    {
        var correlationId = Guid.NewGuid();

        _repository.GetByCorrelationIdAsync(correlationId, "TestSaga", Arg.Any<CancellationToken>())
            .Returns((SagaState?)null);

        var result = await Build(_step1, _step2)
            .ResumeFromStepAsync(new TestSagaData { CorrelationId = correlationId }, new TestSagaContext(), "Step2", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Saga state not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ResumeFromStepAsync_ShouldReturnSuccess_WhenSagaAlreadyCompleted()
    {
        var correlationId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        _repository.GetByCorrelationIdAsync(correlationId, "TestSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                CorrelationId = correlationId,
                Status = SagaStatus.Completed,
                SagaType = "TestSaga",
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext())
            });

        var result = await Build(_step1, _step2)
            .ResumeFromStepAsync(new TestSagaData { CorrelationId = correlationId }, new TestSagaContext(), "Step1", CancellationToken.None);

        // no steps executed
        Assert.True(result.IsSuccess);
        Assert.Equal(sagaId, result.SagaId);

        await _step1.DidNotReceive().ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeFromStepAsync_ShouldFail_WhenSagaIsNotWaitingForEvent()
    {
        var correlationId = Guid.NewGuid();

        _repository.GetByCorrelationIdAsync(correlationId, "TestSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.Running, // not WaitingForEvent and not Completed
                SagaType = "TestSaga",
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext())
            });

        var result = await Build(_step1)
            .ResumeFromStepAsync(new TestSagaData { CorrelationId = correlationId }, new TestSagaContext(), "Step1", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not in waiting state", result.ErrorMessage);
    }

    [Fact]
    public async Task ResumeFromStepAsync_ShouldFail_WhenStepNameNotFound()
    {
        var correlationId = Guid.NewGuid();

        _repository.GetByCorrelationIdAsync(correlationId, "TestSaga", Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Status = SagaStatus.WaitingForEvent,
                SagaType = "TestSaga",
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext())
            });

        _step1.Order.Returns(1); _step1.StepName.Returns("Step1");

        var result = await Build(_step1)
            .ResumeFromStepAsync(new TestSagaData { CorrelationId = correlationId }, new TestSagaContext(), "NonExistentStep", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("NonExistentStep", result.ErrorMessage);
    }
    

    [Fact]
    public async Task CompensateAsync_ShouldCompensateOnlyCompletedSteps_InReverseOrder()
    {
        var sagaId = Guid.NewGuid();

        _step1.Order.Returns(1); _step1.StepName.Returns("Step1");
        _step2.Order.Returns(2); _step2.StepName.Returns("Step2");
        // Step3 exists in saga state logs but is not registered in the saga - must be ignored type shit

        _repository.GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                Status = SagaStatus.Failed,
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext()),
                Steps = new List<SagaStepLog>
                {
                    new() { StepName = "Step1", Status = StepStatus.Completed },
                    new() { StepName = "Step2", Status = StepStatus.Completed },
                    new() { StepName = "Step3", Status = StepStatus.Failed } // not registered → ignored
                }
            });

        var result = await Build(_step1, _step2).CompensateAsync(sagaId, CancellationToken.None);

        Assert.Equal(SagaStatus.Compensated, result.Status);

        // Step3 has no matching registered step = never touched
        // compensation order: Step2 first, then Step1 like reverse you know?
        Received.InOrder(() =>
        {
            _step2.CompensateAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
            _step1.CompensateAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task CompensateAsync_ShouldSkip_WhenAlreadyCompensated()
    {
        var sagaId = Guid.NewGuid();

        _repository.GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                Status = SagaStatus.Compensated,
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext())
            });

        var result = await Build(_step1).CompensateAsync(sagaId, CancellationToken.None);

        Assert.Equal(SagaStatus.Compensated, result.Status);
        await _step1.DidNotReceive().CompensateAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldSetFailedToCompensate_AndEnqueueRetry_WhenCompensationFailsPermanently()
    {
        var sagaId = Guid.NewGuid();

        _step1.Order.Returns(1); _step1.StepName.Returns("Step1");
        _step1.CompensateAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("permanent failure"));

        // non-transient => no retry
        _errorClassifier.IsTransient(Arg.Any<Exception>()).Returns(false);

        _repository.GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                SagaType = "TestSaga",
                Status = SagaStatus.Failed,
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext()),
                Steps = new List<SagaStepLog>
                {
                    new() { StepName = "Step1", Status = StepStatus.Completed }
                }
            });

        var result = await Build(_step1).CompensateAsync(sagaId, CancellationToken.None);

        // FailedToCompensate is now retryable — the method returns instead of throwing
        Assert.Equal(SagaStatus.FailedToCompensate, result.Status);

        await _repository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Status == SagaStatus.FailedToCompensate),
            Arg.Any<CancellationToken>());

        // A retry row must be enqueued so CompensationRetryWorker can recover automatically
        await _failedCompensationRetryRepository.Received(1).EnqueueIfNotExistsAsync(
            sagaId,
            Arg.Any<string>(),
            "Step1",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkTimedOut_AndCompensate_WhenSagaTimeoutExpires()
    {
        // step blocks indefinitely until its cancellation token fires
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = (CancellationToken)callInfo[2];
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // waits until the saga timeout kills it
                return (StepOutcome)new Completed();
            });

        // compensation needs GetByIdAsync; nothing to compensate (no completed steps)
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new SagaState
            {
                Id = (Guid)callInfo[0],
                Status = SagaStatus.TimedOut,
                Payload = JsonSerializer.Serialize(new TestSagaData()),
                Context = JsonSerializer.Serialize(new TestSagaContext()),
                Steps = new List<SagaStepLog>()
            });

        var saga = new TestSagaWithShortTimeout(_repository, new[] { _step1 }, _errorClassifier, _logger, _failedCompensationRetryRepository);
        var result = await saga.ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SagaStatus.TimedOut, result.Status);

        await _repository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Status == SagaStatus.TimedOut),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowOperationCanceledException_WhenExternalCancellationRequested()
    {
        // External cancellation (service shutdown) must NOT be swallowed
        // Only the saga's own internal timeout is caught and converted to TimedOut status
        _step1.Order.Returns(1);
        _step1.StepName.Returns("Step1");
        _step1.ExecuteAsync(Arg.Any<TestSagaData>(), Arg.Any<TestSagaContext>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = (CancellationToken)callInfo[2];
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return (StepOutcome)new Completed();
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(_step1).ExecuteAsync(new TestSagaData { CorrelationId = Guid.NewGuid() }, cts.Token));
    }
}
