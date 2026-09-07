using Application.Common;
using Application.Interfaces;
using MediatR;

namespace Application.Queries.GetTrialBalance;

public sealed record GetTrialBalanceQuery(string ReportingCurrency) : IRequest<Result<TrialBalance>>;

internal sealed class GetTrialBalanceQueryHandler(ILedgerReportingReader reader)
    : IRequestHandler<GetTrialBalanceQuery, Result<TrialBalance>>
{
    public async Task<Result<TrialBalance>> Handle(
        GetTrialBalanceQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ReportingCurrency))
            return Result<TrialBalance>.Failure("ReportingCurrency is required.");

        var balance = await reader.GetTrialBalanceAsync(
            request.ReportingCurrency.Trim().ToUpperInvariant(),
            cancellationToken);

        return Result<TrialBalance>.Success(balance);
    }
}
