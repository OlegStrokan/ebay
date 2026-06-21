using Domain.Common;
using Domain.ValueObjects;

namespace Domain.Events;

public sealed record PaymentAuthorizedEvent(
    PaymentId PaymentId,
    ProviderPaymentIntentId ProviderPaymentIntentId,
    DateTime AuthorizedAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredOn => AuthorizedAt;
}
