using Domain.Common;
using Domain.ValueObjects;

namespace Domain.Events;

public sealed record ProductApprovedEvent(
    ProductId ProductId,
    DateTime ApprovedAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredOn => ApprovedAt;
}
