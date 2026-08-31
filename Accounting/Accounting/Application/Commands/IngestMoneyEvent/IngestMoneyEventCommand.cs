using Application.Common;
using Application.Contracts;
using MediatR;

namespace Application.Commands.IngestMoneyEvent;

public sealed record IngestMoneyEventCommand(MoneyEventPayload Payload) : IRequest<Result<string>>;
