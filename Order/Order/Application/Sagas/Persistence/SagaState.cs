using Application.Sagas.Steps;

namespace Application.Sagas.Persistence;


// for db
public sealed class SagaState
{
    public Guid Id { get; set; }

    public Guid CorrelationId { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public SagaStatus Status { get; set; }
    public string SagaType { get; set; } = string.Empty;
    public string Context { get; set; } = default!;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DateTime? WaitingSinceUtc { get; set; }
    public DateTime? WaitDeadlineUtc { get; set; }
    public string? WaitReason { get; set; }
    public WaitRecovery? WaitRecoveryMode { get; set; }

    public List<SagaStepLog> Steps { get; set; } = new();

    public void MarkWaiting(string stepName, WaitForEvent wait, DateTime nowUtc)
    {
        Status = SagaStatus.WaitingForEvent;
        CurrentStep = stepName;
        UpdatedAt = nowUtc;
        WaitingSinceUtc = nowUtc;
        WaitDeadlineUtc = nowUtc + wait.Deadline;
        WaitReason = wait.Reason;
        WaitRecoveryMode = wait.Recovery;
    }

    public void ClearWait()
    {
        WaitingSinceUtc = null;
        WaitDeadlineUtc = null;
        WaitReason = null;
        WaitRecoveryMode = null;
    }
}

public sealed class SagaStepLog
{
    public Guid Id { get; set; }
    public Guid SagaId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public StepStatus Status { get; set; }
    public string? Request { get; set; }
    public string? Response { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
}


public enum StepStatus
{
    Running,
    Completed,
    Failed,
    Compensated
}