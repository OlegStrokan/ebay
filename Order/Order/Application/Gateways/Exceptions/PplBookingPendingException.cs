namespace Application.Gateways.Exceptions;

public sealed class PplBookingPendingException(string referenceId, Guid orderId)
    : Exception($"PPL booking {referenceId} for order {orderId} did not settle within the polling window.")
{
    public string ReferenceId { get; } = referenceId;
    public Guid OrderId { get; } = orderId;
}
