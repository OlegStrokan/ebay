using Application.Sagas;
using Application.Sagas.Persistence;
using FluentAssertions;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Order.IntegrationTests.Infrastructure;
using Xunit;

namespace Order.IntegrationTests.Sagas;

[Collection("Integration")]
public sealed class SagaRepositoryTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public SagaRepositoryTests(IntegrationFixture fixture) => _fixture = fixture;
    
    private static SagaState BuildSaga(SagaStatus status = SagaStatus.Running) => new()
    {
        Id            = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        SagaType      = "OrderSaga",
        CurrentStep   = "PaymentStep",
        Status        = status,
        Context       = "{}",
        Payload       = "{}",
        CreatedAt     = DateTime.UtcNow,
        UpdatedAt     = DateTime.UtcNow
    };
    
    [Fact]
    public async Task SaveSagaStateAsync_ShouldPersistAndReload_WithCorrectStatus()
    { 
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var saga = BuildSaga(SagaStatus.Running);
        saga.Context = """{"orderId":"abc-123","step":"init"}""";
        
        await repo.SaveAsync(saga, CancellationToken.None);

        // reload in a fresh scope to bypass EF cache
        await scope.DisposeAsync();
        await using var loadScope = _fixture.CreateScope();
        var loadRepo = loadScope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var loaded = await loadRepo.GetByIdAsync(saga.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(saga.Id);
        loaded.Status.Should().Be(SagaStatus.Running);
        loaded.SagaType.Should().Be("OrderSaga");
        loaded.CurrentStep.Should().Be("PaymentStep");
        loaded.Context.Should().Be(saga.Context, "JSON context must round trip unchanged");
    }


    [Fact]
    public async Task UpdateSagaState_ShouldTransitionStatus_AndRetainStepData()
    {
        // create Running saga with one step
        await using var setupScope = _fixture.CreateScope();
        var repo = setupScope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var saga = BuildSaga(SagaStatus.Running);
        await repo.SaveAsync(saga, CancellationToken.None);

        var step = new SagaStepLog
        {
            Id        = Guid.NewGuid(),
            SagaId    = saga.Id,
            StepName  = "PaymentStep",
            Status    = StepStatus.Completed,
            Request   = """{"amount":99}""",
            Response  = """{"paymentId":"PAY-001"}""",
            StartedAt = DateTime.UtcNow.AddSeconds(-2),
            CompletedAt = DateTime.UtcNow
        };
        await repo.SaveStepAsync(step, CancellationToken.None);

        saga.Status     = SagaStatus.Completed;
        saga.UpdatedAt  = DateTime.UtcNow;
        await repo.SaveAsync(saga, CancellationToken.None);

        await setupScope.DisposeAsync();
        await using var assertScope = _fixture.CreateScope();
        var assertRepo = assertScope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var loaded = await assertRepo.GetByIdAsync(saga.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(SagaStatus.Completed, "status must reflect the transition");

        var steps = await assertRepo.GetStepLogsAsync(saga.Id, CancellationToken.None);
        steps.Should().ContainSingle(s => s.Id == step.Id,
            "the step log must be retained after the saga transition");
        steps[0].Response.Should().Be(step.Response, "step response JSON must be preserved");
    }

    // A re-run step used to insert a second row, so compensation marked one and left the other
    // Completed for the retry worker to compensate again.
    [Fact]
    public async Task SaveStepAsync_ShouldUpsertOnSagaIdAndStepName_NotInsertDuplicate()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var saga = BuildSaga(SagaStatus.WaitingForEvent);
        await repo.SaveAsync(saga, CancellationToken.None);

        await repo.SaveStepAsync(
            new SagaStepLog
            {
                Id = Guid.NewGuid(),
                SagaId = saga.Id,
                StepName = "CapturePayment",
                Status = StepStatus.Waiting,
                StartedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            CancellationToken.None);

        // same step runs again after the saga resumes - fresh Guid, same (SagaId, StepName)
        await repo.SaveStepAsync(
            new SagaStepLog
            {
                Id = Guid.NewGuid(),
                SagaId = saga.Id,
                StepName = "CapturePayment",
                Status = StepStatus.Completed,
                Response = """{"paymentId":"PAY-002"}""",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            },
            CancellationToken.None);

        var steps = await repo.GetStepLogsAsync(saga.Id, CancellationToken.None);

        steps.Should().ContainSingle(s => s.StepName == "CapturePayment",
            "a re-run step must update its row, not add a second one");
        steps.Single(s => s.StepName == "CapturePayment").Status.Should().Be(StepStatus.Completed);
        steps.Single(s => s.StepName == "CapturePayment").Response.Should().Be("""{"paymentId":"PAY-002"}""");
    }
}
