using System.Text.Json;
using Application.Common.Enums;
using Application.DTOs;
using Application.Gateways;
using Application.Sagas.Handlers;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga;

// Fails a parked saga when Inventory releases the reservation out from under it.
public sealed class InventoryExpiredEventHandler(
    IOrderSaga saga,
    ISagaRepository sagaRepository,
    ISagaDistributedLock distributedLock,
    IIncidentReporter incidentReporter,
    ILogger<InventoryExpiredEventHandler> logger) : ISagaEventHandler
{
    // Inventory's payload is camelCase; Order's own events are PascalCase. Explicit here so the
    // difference is a stated fact rather than something that silently binds to default values.
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string EventType => "InventoryExpired";
    public string SagaType => SagaTypes.OrderSaga;

    public async Task HandleAsync(string eventPayload, CancellationToken cancellationToken)
    {
        InventoryExpiredEventDto? eventDto;

        try
        {
            eventDto = JsonSerializer.Deserialize<InventoryExpiredEventDto>(eventPayload, PayloadOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize InventoryExpired. Invalid JSON format.");
            return;
        }

        if (eventDto is null || !Guid.TryParse(eventDto.OrderId, out var correlationId))
        {
            logger.LogWarning(
                "InventoryExpired carries no parseable OrderId ('{OrderId}'). Cannot locate a saga.",
                eventDto?.OrderId);
            return;
        }

        // Same key the forward, resume, watchdog and ops-console paths use, so this can never
        // compensate concurrently with an in-flight resume
        var lockKey = $"saga-lock:{SagaType}:{correlationId}";
        var lockExpiry = saga.LockBudget + TimeSpan.FromMinutes(1);

        await using var lockHandle = await distributedLock.TryAcquireAsync(lockKey, lockExpiry, cancellationToken);
        if (lockHandle is null)
        {
            logger.LogWarning(
                "Could not acquire lock for {SagaType} {CorrelationId} while handling InventoryExpired. " +
                "A concurrent resume holds it; the saga watchdog will reap the saga on its wait deadline.",
                SagaType, correlationId);
            return;
        }

        var sagaState = await sagaRepository.GetByCorrelationIdAsync(correlationId, SagaType, cancellationToken);

        if (sagaState is null)
        {
            logger.LogInformation(
                "No {SagaType} for correlation {CorrelationId}; InventoryExpired needs no action " +
                "(the reservation outlived its saga).",
                SagaType, correlationId);
            return;
        }

        // A reservation released by the saga's own compensation is reported as Released, not
        // Expired - so an Expired reservation for a saga that already finished means the two views
        // disagree and a human should look, but there is nothing left to fail.
        if (sagaState.Status is not (SagaStatus.WaitingForEvent or SagaStatus.Running))
        {
            logger.LogInformation(
                "Saga {SagaId} is {Status}; InventoryExpired for reservation {ReservationId} needs no action.",
                sagaState.Id, sagaState.Status, eventDto.ReservationId);
            return;
        }

        logger.LogError(
            "Inventory released reservation {ReservationId} while {SagaType} {SagaId} was still {Status} " +
            "at step {CurrentStep}. The saga can no longer fulfil this order. Failing and compensating.",
            eventDto.ReservationId, SagaType, sagaState.Id, sagaState.Status, sagaState.CurrentStep);

        await incidentReporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "SagaInventoryExpiredWhileWaiting",
                OrderId: correlationId,
                RefundId: null,
                Message: $"Inventory reservation {eventDto.ReservationId} expired while {SagaType} " +
                         $"{sagaState.Id} was {sagaState.Status} at {sagaState.CurrentStep} " +
                         $"(waiting for {sagaState.WaitReason ?? "n/a"}). The saga's wait deadline did not " +
                         "fire before Inventory's reservation TTL - check that they are still consistent.",
                Severity: AlertSeverity.Critical),
            cancellationToken);

        sagaState.Status = SagaStatus.Failed;
        sagaState.UpdatedAt = DateTime.UtcNow;
        sagaState.ClearWait();
        await sagaRepository.SaveAsync(sagaState, cancellationToken);

        try
        {
            // Compensation unwinds whatever did happen - including voiding or refunding the
            // payment, which is the part that must not be skipped.
            await saga.CompensateAsync(sagaState.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "CRITICAL: Failed to compensate saga {SagaId} after InventoryExpired. Manual intervention required!",
                sagaState.Id);

            sagaState.Status = SagaStatus.FailedToCompensate;
            sagaState.UpdatedAt = DateTime.UtcNow;
            await sagaRepository.SaveAsync(sagaState, cancellationToken);
        }
    }
}
