using Application.Common;
using MediatR;

namespace Application.Commands.RecordRefund;

public sealed record RecordRefundCommand(
    Guid OrderId,
    string RefundId,
    decimal Amount,
    string Currency,
    string Reason) : IRequest<Result<string>>;
