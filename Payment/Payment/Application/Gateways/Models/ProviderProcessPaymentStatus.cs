namespace Application.Gateways.Models;

public enum ProviderProcessPaymentStatus
{
    Succeeded = 0,
    Pending = 1,
    Failed = 2,
    RequiresAction = 3,
    RequiresCapture = 4,
}