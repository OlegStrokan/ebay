using Application.Common;
using Application.Interfaces;
using MediatR;

namespace Application.Queries.GetOrderMoneyTrail;

public sealed record GetOrderMoneyTrailQuery(Guid OrderId)
    : IRequest<Result<IReadOnlyList<MoneyTrailTransaction>>>;

internal sealed class GetOrderMoneyTrailQueryHandler(ILedgerReportingReader reader)
    : IRequestHandler<GetOrderMoneyTrailQuery, Result<IReadOnlyList<MoneyTrailTransaction>>>
{
    public async Task<Result<IReadOnlyList<MoneyTrailTransaction>>> Handle(
        GetOrderMoneyTrailQuery request,
        CancellationToken cancellationToken)
    {
        if (request.OrderId == Guid.Empty)
            return Result<IReadOnlyList<MoneyTrailTransaction>>.Failure("A valid OrderId is required.");

        var trail = await reader.GetOrderMoneyTrailAsync(request.OrderId, cancellationToken);
        return Result<IReadOnlyList<MoneyTrailTransaction>>.Success(trail);
    }
}
