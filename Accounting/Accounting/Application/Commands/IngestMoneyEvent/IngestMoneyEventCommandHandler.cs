using Application.Common;
using Application.Contracts;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.IngestMoneyEvent;

// Turns one Payment money-event into one balanced ledger transaction
internal sealed class IngestMoneyEventCommandHandler(
    ILedgerTransactionRepository ledgerRepository,
    IProcessedEventRepository processedEventRepository,
    IUnitOfWork unitOfWork,
    ILogger<IngestMoneyEventCommandHandler> logger)
    : IRequestHandler<IngestMoneyEventCommand, Result<string>>
{
    public async Task<Result<string>> Handle(IngestMoneyEventCommand request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;

        var validationError = Validate(payload);
        if (validationError is not null)
            return Result<string>.Failure(validationError);

        if (await processedEventRepository.ExistsAsync(payload.EventId, cancellationToken))
        {
            logger.LogInformation(
                "Money event {EventId} of type {EventType} was already ingested. Skipping.",
                payload.EventId,
                payload.EventType);

            return Result<string>.Success("duplicate");
        }

        LedgerTransaction transaction;
        try
        {
            transaction = BuildTransaction(payload);
        }
        catch (Exception ex) when (ex is ArgumentException or UnbalancedTransactionException)
        {
            return Result<string>.Failure(ex.Message);
        }

        var processedEvent = ProcessedEvent.Create(payload.EventId, payload.EventType, DateTime.UtcNow);

        var existing = await ledgerRepository.GetByTransactionRefAsync(transaction.TransactionRef, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Ledger transaction {TransactionRef} already exists. Recording money event {EventId} as processed without a second posting.",
                transaction.TransactionRef,
                payload.EventId);

            await processedEventRepository.AddAsync(processedEvent, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<string>.Success(existing.Id.ToString());
        }

        try
        {
            await ledgerRepository.AddAsync(transaction, cancellationToken);
            await processedEventRepository.AddAsync(processedEvent, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateLedgerTransactionException ex)
        {
            // A concurrent consumer or the gRPC path won the race. Either way the ledger holds
            // exactly one posting, which is the outcome this handler wants.
            logger.LogInformation(
                ex,
                "Money event {EventId} collided with an existing posting for {TransactionRef}. Treating as an idempotent success.",
                payload.EventId,
                transaction.TransactionRef);

            return Result<string>.Success("duplicate");
        }

        logger.LogInformation(
            "Posted money event {EventId} of type {EventType} as ledger transaction {TransactionId} ({TransactionRef}).",
            payload.EventId,
            payload.EventType,
            transaction.Id,
            transaction.TransactionRef);

        return Result<string>.Success(transaction.Id.ToString());
    }

    private static string? Validate(MoneyEventPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.EventId))
            return "EventId is required.";

        if (string.IsNullOrWhiteSpace(payload.PaymentId))
            return "PaymentId is required.";

        if (string.IsNullOrWhiteSpace(payload.Currency))
            return "Currency is required.";

        if (payload.Amount <= 0m)
            return $"Amount must be positive but was {payload.Amount}.";

        return null;
    }

    private static LedgerTransaction BuildTransaction(MoneyEventPayload payload)
    {
        var orderId = ParseOptionalGuid(payload.OrderId);
        var paymentId = ParseOptionalGuid(payload.PaymentId);
        var amount = new Money(payload.Amount, payload.Currency);

        return payload.EventType switch
        {
            MoneyEventTypes.PaymentAuthorized => LedgerTransaction.ForAuthorization(
                orderId, paymentId, payload.PaymentId, amount, payload.OccurredAt),

            MoneyEventTypes.PaymentVoided => LedgerTransaction.ForAuthorizationVoid(
                orderId, paymentId, payload.PaymentId, amount, payload.OccurredAt),

            MoneyEventTypes.PaymentCaptured => LedgerTransaction.ForCapture(
                orderId, paymentId, payload.PaymentId, amount, payload.Fee, payload.Tax, payload.OccurredAt),

            MoneyEventTypes.RefundIssued => LedgerTransaction.ForRefund(
                orderId,
                payload.RefundId ?? throw new ArgumentException(
                    $"{MoneyEventTypes.RefundIssued} carries no refundId, so the posting cannot be made idempotent."),
                amount,
                payload.OccurredAt,
                paymentId),

            _ => throw new ArgumentException($"Unsupported money event type '{payload.EventType}'."),
        };
    }

    // Payment ids are Guids serialised without dashes, but the column is nullable on purpose:
    // a non-Guid id from an older record should not stop the posting.
    private static Guid? ParseOptionalGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;
}
