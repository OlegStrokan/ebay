using Application.Common;
using MediatR;

namespace Application.Commands.ReverseRevenue;

public sealed record ReverseRevenueCommand(
    Guid OrderId,
    Guid ReturnRequestId,
    decimal Amount,
    string Currency) : IRequest<Result<string>>;
