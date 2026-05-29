using Domain.Common;
using Domain.ValueObjects;

namespace Domain.Events;

public sealed record ProductRejectedEvent(
    ProductId ProductId,
    string Reason,
    DateTime RejectedAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredOn => RejectedAt;
}
