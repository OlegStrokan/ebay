using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.CancelReversal;

internal sealed class CancelReversalCommandHandler(
    ILedgerTransactionRepository repository,
    IUnitOfWork unitOfWork,
    ILogger<CancelReversalCommandHandler> logger)
    : IRequestHandler<CancelReversalCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(CancelReversalCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ReversalId) || !Guid.TryParse(request.ReversalId, out var reversalId))
            return Result<bool>.Failure("A valid ReversalId is required.");

        var cancellationRef = $"cancel-reversal:{reversalId}";

        var alreadyCancelled = await repository.GetByTransactionRefAsync(cancellationRef, cancellationToken);
        if (alreadyCancelled is not null)
        {
            logger.LogInformation(
                "CancelReversal is idempotent no-op. ReversalId={ReversalId}",
                reversalId);
            return Result<bool>.Success(true);
        }

        var original = await repository.GetByIdAsync(reversalId, cancellationToken);
        if (original is null)
            return Result<bool>.Failure($"Revenue reversal {reversalId} was not found.");

        var cancellation = LedgerTransaction.ForReversalCancellation(original, DateTime.UtcNow);

        try
        {
            await repository.AddAsync(cancellation, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateLedgerTransactionException)
        {
            logger.LogInformation(
                "CancelReversal collapsed to existing cancellation. ReversalId={ReversalId}",
                reversalId);
            return Result<bool>.Success(true);
        }

        logger.LogInformation(
            "Revenue reversal cancelled in ledger. ReversalId={ReversalId}, CancellationId={CancellationId}, Reason={Reason}",
            reversalId,
            cancellation.Id,
            request.Reason);

        return Result<bool>.Success(true);
    }
}
