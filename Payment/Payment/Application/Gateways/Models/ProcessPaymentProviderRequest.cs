using Domain.Enums;

namespace Application.Gateways.Models;

public sealed record ProcessPaymentProviderRequest(
    string PaymentId,
    string OrderId,
    string CustomerId,
    decimal Amount,
    string Currency,
    PaymentMethod PaymentMethod,
    string IdempotencyKey,
    string? CustomerEmail,
    bool ManualCapture = false);