namespace Application.DTOs;

public enum ProcessPaymentStatus
{
    Succeeded = 0,
    Pending = 1,
    Failed = 2,
    RequiresAction = 3,
    Authorized = 4,
}