
namespace Application.Gateways;

public interface IPaymentGateway
{
    Task<PaymentProcessingResult> ProcessPaymentAsync(
        Guid orderId, 
        Guid customerId,
        decimal amount,  
        string currency,
        string paymentMethod,
        CancellationToken cancellationToken
        );

    Task<PaymentProcessingResult> AuthorizeAsync(
        Guid orderId,
        Guid customerId,
        decimal amount,
        string currency,
        string paymentMethod,
        CancellationToken cancellationToken
        );

    Task<PaymentProcessingResult> CaptureAsync(
        Guid orderId,
        Guid customerId,
        string providerPaymentIntentId,
        decimal amount,
        string currency,
        CancellationToken cancellationToken);

    Task CancelAuthorizationAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken);
    
    Task<string> RefundAsync(
        string paymentId, 
        decimal amount,
        string currency,
        string reason,
        CancellationToken cancellationToken
        );

    Task<RefundProcessingResult> RefundWithStatusAsync(
        string paymentId,
        decimal amount,
        string currency,
        string reason,
        CancellationToken cancellationToken
        );
}

public enum PaymentProcessingStatus
{
    Succeeded = 0,
    Pending = 1,
    RequiresAction = 2,
    Failed = 3,
    Authorized = 4,
}

public enum RefundProcessingStatus
{
    Succeeded = 0,
    Pending = 1,
}

public sealed record PaymentProcessingResult(
    string? PaymentId,
    PaymentProcessingStatus Status,
    string? ProviderPaymentIntentId = null,
    string? ClientSecret = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record RefundProcessingResult(
    string RefundId,
    RefundProcessingStatus Status,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ProviderRefundId = null);