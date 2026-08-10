namespace Application.Models;

public sealed class AdminActionAuditEntry
{
    public Guid Id { get; private set; }
    public string Action { get; private set; } = string.Empty; // CompensateSaga | RetryCompensation | RequeueDeadLetter
    public string TargetId { get; private set; } = string.Empty; // SagaId or dead-letter MessageId
    public string OperatorSubject { get; private set; } = string.Empty;
    public bool Success { get; private set; }
    public string? Detail { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private AdminActionAuditEntry() { }

    private AdminActionAuditEntry(
        Guid id,
        string action,
        string targetId,
        string operatorSubject,
        bool success,
        string? detail,
        DateTime occurredAtUtc)
    {
        Id = id;
        Action = action;
        TargetId = targetId;
        OperatorSubject = operatorSubject;
        Success = success;
        Detail = detail;
        OccurredAtUtc = occurredAtUtc;
    }

    public static AdminActionAuditEntry Create(
        string action,
        string targetId,
        string operatorSubject,
        bool success,
        string? detail,
        DateTime? occurredAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action cannot be empty.", nameof(action));
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("TargetId cannot be empty.", nameof(targetId));

        return new AdminActionAuditEntry(
            Guid.NewGuid(),
            action.Trim(),
            targetId.Trim(),
            string.IsNullOrWhiteSpace(operatorSubject) ? "unknown" : operatorSubject.Trim(),
            success,
            detail?.Trim(),
            occurredAtUtc ?? DateTime.UtcNow);
    }
}
