using Application.Common;
using Application.DTOs;
using Domain.Enums;

namespace Application.Commands.ProcessPayment;

public sealed record ProcessPaymentCommand(
    string OrderId,
    string CustomerId,
    decimal Amount,
    string Currency,
    PaymentMethod PaymentMethod,
    string IdempotencyKey,
    string? CustomerEmail,
    bool ManualCapture = false) : ICommand<Result<ProcessPaymentResultDto>>;