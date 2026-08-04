using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.RecordRefund;

internal sealed class RecordRefundCommandHandler(
    ILedgerTransactionRepository repository,
    IUnitOfWork unitOfWork,
    ILogger<RecordRefundCommandHandler> logger)
    : IRequestHandler<RecordRefundCommand, Result<string>>
{
    public async Task<Result<string>> Handle(RecordRefundCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefundId))
            return Result<string>.Failure("RefundId is required.");

        if (request.Amount <= 0m)
            return Result<string>.Failure("Refund amount must be positive.");

        var transactionRef = $"refund:{request.RefundId.Trim()}";

        var existing = await repository.GetByTransactionRefAsync(transactionRef, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "RecordRefund is idempotent no-op. RefundId={RefundId}, TransactionId={TransactionId}",
                request.RefundId,
                existing.Id);
            return Result<string>.Success(existing.Id.ToString());
        }

        var transaction = LedgerTransaction.ForRefund(
            request.OrderId,
            request.RefundId.Trim(),
            new Money(request.Amount, request.Currency),
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
            "Refund recorded in ledger. OrderId={OrderId}, RefundId={RefundId}, TransactionId={TransactionId}",
            request.OrderId,
            request.RefundId,
            transaction.Id);

        return Result<string>.Success(transaction.Id.ToString());
    }
}
