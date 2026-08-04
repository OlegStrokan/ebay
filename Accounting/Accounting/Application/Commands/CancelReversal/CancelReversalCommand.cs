using Application.Common;
using MediatR;

namespace Application.Commands.CancelReversal;

public sealed record CancelReversalCommand(
    string ReversalId,
    string Reason) : IRequest<Result<bool>>;
