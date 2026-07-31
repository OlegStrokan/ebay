namespace Application.Sagas.Steps;

public interface ISagaStep<TData, TContext>
    where TData : SagaData
    where TContext : SagaContext
{
    string StepName { get; }
    int Order { get; } // explicit order
    Task<StepOutcome> ExecuteAsync(TData data, TContext context, CancellationToken cancellationToken);
    Task CompensateAsync(TData data, TContext context, CancellationToken cancellationToken);
}

public abstract record StepOutcome;

public record Completed(Dictionary<string, object>? Data = null) : StepOutcome;

// the step parked the saga until an external event arrives
public record WaitForEvent(string Reason, TimeSpan Deadline, WaitRecovery Recovery) : StepOutcome;

// how a parked saga is expected to be un-parked
public enum WaitRecovery
{
    // the provider/carrier owes us a push (webhook)
    AwaitPush,
    // we do not know whether the call landed. Money may already have moved. If the deadline
    // passes the saga must compensate through the Uncertain safety path rather than silently keep waiting
    ActiveVerify,
}

public record Fail(string Reason) : StepOutcome;