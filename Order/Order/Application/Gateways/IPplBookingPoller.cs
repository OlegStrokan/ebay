namespace Application.Gateways;

public enum PplBookingPollStatus
{
    Pending,
    Accepted,
    Rejected,
}

public record PplBookingPollResult(
    PplBookingPollStatus Status,
    string? ParcelId,
    string? TrackingNumber,
    string? Reason);

public interface IPplBookingPoller
{
    Task<PplBookingPollResult> PollAsync(string referenceId, CancellationToken cancellationToken);
}
