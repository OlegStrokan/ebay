using Application.Common;
using MediatR;

namespace Application.Commands.ReverseRevenue;

public sealed record ReverseRevenueCommand(
    Guid OrderId,
    decimal Amount,
    string Currency) : IRequest<Result<string>>;
