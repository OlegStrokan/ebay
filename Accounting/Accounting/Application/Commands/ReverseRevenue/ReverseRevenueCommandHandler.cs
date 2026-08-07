using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.ReverseRevenue;

internal sealed class ReverseRevenueCommandHandler(
    ILedgerTransactionRepository repository,
    IUnitOfWork unitOfWork,
    ILogger<ReverseRevenueCommandHandler> logger)
    : IRequestHandler<ReverseRevenueCommand, Result<string>>
{
    public async Task<Result<string>> Handle(ReverseRevenueCommand request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0m)
            return Result<string>.Failure("Reversal amount must be positive.");

        if (request.ReturnRequestId == Guid.Empty)
            return Result<string>.Failure("ReturnRequestId is required to reverse revenue.");

        var money = new Money(request.Amount, request.Currency);
      
        var transactionRef = $"reversal:{request.ReturnRequestId}";

        var existing = await repository.GetByTransactionRefAsync(transactionRef, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "ReverseRevenue is idempotent no-op. OrderId={OrderId}, ReturnRequestId={ReturnRequestId}, ReversalId={ReversalId}",
                request.OrderId,
                request.ReturnRequestId,
                existing.Id);
            return Result<string>.Success(existing.Id.ToString());
        }

        var transaction = LedgerTransaction.ForRevenueReversal(
            request.OrderId,
            request.ReturnRequestId,
            money,
            DateTime.UtcNow);

        try
        {
            await repository.AddAsync(transaction, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateLedgerTransactionException)
        {
            var concurrent = await repository.GetByTransactionRefAsync(transactionRef, cancellationToken);
            if (concurrent is not null)
                return Result<string>.Success(concurrent.Id.ToString());
            throw;
        }

        logger.LogInformation(
            "Revenue reversed in ledger. OrderId={OrderId}, ReturnRequestId={ReturnRequestId}, ReversalId={ReversalId}, Amount={Amount} {Currency}",
            request.OrderId,
            request.ReturnRequestId,
            transaction.Id,
            money.Amount,
            money.Currency);

        return Result<string>.Success(transaction.Id.ToString());
    }
}
