using System.Data;
using System.Text.Json;
using Application.Interfaces;
using Application.Models;
using Infrastructure.Messaging;
using Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.Persistence;

// Coordinates inventory reservation state transitions inside one transactional boundary.
// Each operation updates reservation state, stock quantities, movement history, and outbox events together.
// Serializable retries create a fresh DbContext per attempt so each retry starts from a clean EF unit of work.
public sealed class InventoryReservationStore(
    IDbContextFactory<InventoryDbContext> dbContextFactory,
    IOptions<KafkaOptions> kafkaOptions,
    ILogger<InventoryReservationStore> logger) : IInventoryReservationStore
{
    private const int MaxSerializableRetryAttempts = 3;
    private const string SerializationFailureSqlState = "40001";
    private const string DeadlockDetectedSqlState = "40P01";

    private readonly string inventoryEventsTopic = string.IsNullOrWhiteSpace(kafkaOptions.Value.InventoryEventsTopic)
        ? "inventory.events"
        : kafkaOptions.Value.InventoryEventsTopic;

    public Task<ReserveInventoryResult> ReserveAsync(
        Guid orderId,
        IReadOnlyCollection<ReserveStockItem> items,
        CancellationToken cancellationToken)
    {
        return ExecuteWithSerializableRetryAsync(ReserveOperationAsync, "ReserveInventory", cancellationToken);

        Task<ReserveInventoryResult> ReserveOperationAsync(
            InventoryDbContext dbContext,
            CancellationToken operationCancellationToken)
            => ReserveInternalAsync(dbContext, orderId, items, operationCancellationToken);
    }

    public Task<ReleaseInventoryResult> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        return ExecuteWithSerializableRetryAsync(ConfirmOperationAsync, "ConfirmInventory", cancellationToken);

        Task<ReleaseInventoryResult> ConfirmOperationAsync(
            InventoryDbContext dbContext,
            CancellationToken operationCancellationToken)
            => ConfirmInternalAsync(dbContext, reservationId, operationCancellationToken);
    }

    public Task<ReleaseInventoryResult> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        return ExecuteWithSerializableRetryAsync(ReleaseOperationAsync, "ReleaseInventory", cancellationToken);

        Task<ReleaseInventoryResult> ReleaseOperationAsync(
            InventoryDbContext dbContext,
            CancellationToken operationCancellationToken)
            => ReleaseInternalAsync(dbContext, reservationId, operationCancellationToken);
    }

    public async Task<int> ExpireStaleReservationsAsync(
        DateTime olderThanUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var candidateIds = await dbContext.InventoryReservations
            .AsNoTracking()
            .Where(x => x.Status == ReservationStatus.Active && x.CreatedAtUtc <= olderThanUtc)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.ReservationId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var expiredCount = 0;

        foreach (var reservationId in candidateIds)
        {
            var result = await ExpireReservationAsync(reservationId, cancellationToken);

            if (result.Success && !result.IsIdempotentReplay)
                expiredCount++;
        }

        return expiredCount;
    }

    private Task<ReleaseInventoryResult> ExpireReservationAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        return ExecuteWithSerializableRetryAsync(ExpireOperationAsync, "ExpireInventoryReservation", cancellationToken);

        Task<ReleaseInventoryResult> ExpireOperationAsync(
            InventoryDbContext dbContext,
            CancellationToken operationCancellationToken)
            => ExpireInternalAsync(dbContext, reservationId, operationCancellationToken);
    }
    
    private async Task<TResult> ExecuteWithSerializableRetryAsync<TResult>(
        Func<InventoryDbContext, CancellationToken, Task<TResult>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxSerializableRetryAttempts; attempt++)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            try
            {
                var result = await operation(dbContext, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (PostgresException ex)
                when (attempt < MaxSerializableRetryAttempts && IsRetryableTransactionConflict(ex))
            {
                await transaction.RollbackAsync(cancellationToken);

                logger.LogWarning(
                    ex,
                    "{Operation} transaction conflict ({SqlState}). Retry attempt {Attempt}/{MaxAttempts}.",
                    operationName,
                    ex.SqlState,
                    attempt,
                    MaxSerializableRetryAttempts);

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        throw new InvalidOperationException($"{operationName} transaction failed after maximum retry attempts.");
    }

    private static bool IsRetryableTransactionConflict(PostgresException ex)
    {
        return ex.SqlState is SerializationFailureSqlState or DeadlockDetectedSqlState;
    }

    private async Task<ReserveInventoryResult> ReserveInternalAsync(
        InventoryDbContext dbContext,
        Guid orderId,
        IReadOnlyCollection<ReserveStockItem> items,
        CancellationToken cancellationToken)
    {
        var existingReservation = await dbContext.InventoryReservations
            .AsNoTracking()
            .Where(x => x.OrderId == orderId)
            .Select(x => new { x.ReservationId })
            .FirstOrDefaultAsync(cancellationToken);

        if (existingReservation is not null)
        {
            logger.LogInformation(
                "ReserveInventory idempotency hit. OrderId={OrderId}, ReservationId={ReservationId}",
                orderId,
                existingReservation.ReservationId);

            return ReserveInventoryResult.Successful(
                existingReservation.ReservationId.ToString(),
                isIdempotentReplay: true);
        }

        var productIds = items.Select(x => x.ProductId).Distinct().ToList();
        var stocks = await LoadStocksByProductIdsAsync(dbContext, productIds, cancellationToken);

        if (stocks.Count != productIds.Count)
        {
            var missingProductId = productIds.First(id => !stocks.ContainsKey(id));

            return ReserveInventoryResult.Failed(
                $"Product stock not found. ProductId={missingProductId}",
                ReserveInventoryFailureReason.ProductNotFound);
        }

        foreach (var item in items)
        {
            var stock = stocks[item.ProductId];

            if (stock.AvailableQuantity < item.Quantity)
            {
                return ReserveInventoryResult.Failed(
                    $"Insufficient stock for ProductId={item.ProductId}. Available={stock.AvailableQuantity}, Requested={item.Quantity}",
                    ReserveInventoryFailureReason.InsufficientStock);
            }
        }

        var reservationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var reservation = new InventoryReservationEntity
        {
            ReservationId = reservationId,
            OrderId = orderId,
            Status = ReservationStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        foreach (var item in items)
        {
            var stock = stocks[item.ProductId];

            stock.AvailableQuantity -= item.Quantity;
            stock.ReservedQuantity += item.Quantity;
            stock.UpdatedAtUtc = now;

            reservation.Items.Add(new InventoryReservationItemEntity
            {
                ReservationItemId = Guid.NewGuid(),
                ReservationId = reservationId,
                ProductId = item.ProductId,
                Quantity = item.Quantity
            });

            AddInventoryMovement(
                dbContext,
                item.ProductId,
                MovementType.Reserve,
                -item.Quantity,
                orderId.ToString(),
                now);
        }

        dbContext.InventoryReservations.Add(reservation);

        AddOutboxMessage(
            dbContext,
            eventType: "InventoryReserved",
            payload: BuildInventoryReservedPayload(reservationId, orderId, items, now),
            createdAtUtc: now);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inventory reserved. OrderId={OrderId}, ReservationId={ReservationId}, ItemCount={ItemCount}",
            orderId,
            reservationId,
            items.Count);

        return ReserveInventoryResult.Successful(reservationId.ToString());
    }

    private async Task<ReleaseInventoryResult> ConfirmInternalAsync(
        InventoryDbContext dbContext,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var reservation = await LoadReservationWithItemsAsync(dbContext, reservationId, cancellationToken);

        if (reservation is null)
        {
            logger.LogWarning(
                "ConfirmInventory failed: reservation not found. ReservationId={ReservationId}",
                reservationId);

            return ReleaseInventoryResult.Failed("Reservation not found.");
        }

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            logger.LogInformation(
                "ConfirmInventory idempotent success: reservation already confirmed. ReservationId={ReservationId}",
                reservationId);

            return ReleaseInventoryResult.Successful(
                isIdempotentReplay: true,
                message: "Reservation already confirmed.");
        }

        if (reservation.Status is ReservationStatus.Released or ReservationStatus.Expired)
        {
            logger.LogWarning(
                "ConfirmInventory failed: reservation is in terminal state {Status}. ReservationId={ReservationId}",
                reservation.Status,
                reservationId);

            return ReleaseInventoryResult.Failed(
                $"Reservation cannot be confirmed from status '{reservation.Status}'.");
        }

        var now = DateTime.UtcNow;
        var productIds = reservation.Items.Select(x => x.ProductId).Distinct().ToList();
        var stocks = await LoadStocksByProductIdsAsync(dbContext, productIds, cancellationToken);

        if (stocks.Count != productIds.Count)
        {
            var missingProductId = productIds.First(id => !stocks.ContainsKey(id));
            return ReleaseInventoryResult.Failed($"Product stock not found. ProductId={missingProductId}");
        }

        foreach (var item in reservation.Items)
        {
            var stock = stocks[item.ProductId];
            stock.ReservedQuantity = Math.Max(0, stock.ReservedQuantity - item.Quantity);
            stock.UpdatedAtUtc = now;

            AddInventoryMovement(
                dbContext,
                item.ProductId,
                MovementType.Confirm,
                0,
                reservationId.ToString(),
                now);
        }

        reservation.Status = ReservationStatus.Confirmed;
        reservation.UpdatedAtUtc = now;

        AddOutboxMessage(
            dbContext,
            eventType: "InventoryConfirmed",
            payload: BuildInventoryReservationPayload(reservation, now),
            createdAtUtc: now);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inventory confirmed. ReservationId={ReservationId}",
            reservationId);

        return ReleaseInventoryResult.Successful();
    }

    private async Task<ReleaseInventoryResult> ReleaseInternalAsync(
        InventoryDbContext dbContext,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var reservation = await LoadReservationWithItemsAsync(dbContext, reservationId, cancellationToken);

        if (reservation is null)
        {
            logger.LogWarning(
                "ReleaseInventory idempotent success: reservation not found. ReservationId={ReservationId}",
                reservationId);

            return ReleaseInventoryResult.Successful(
                isIdempotentReplay: true,
                message: "Reservation not found. Treated as idempotent success.");
        }

        if (reservation.Status is ReservationStatus.Released or ReservationStatus.Expired)
        {
            logger.LogInformation(
                "ReleaseInventory idempotent success: reservation already in terminal release state {Status}. ReservationId={ReservationId}",
                reservation.Status,
                reservationId);

            return ReleaseInventoryResult.Successful(
                isIdempotentReplay: true,
                message: $"Reservation already {reservation.Status.ToLowerInvariant()}.");
        }

        var now = DateTime.UtcNow;

        await RestoreStockForReservationAsync(dbContext, reservation, now, cancellationToken);

        reservation.Status = ReservationStatus.Released;
        reservation.UpdatedAtUtc = now;

        AddOutboxMessage(
            dbContext,
            eventType: "InventoryReleased",
            payload: BuildInventoryReservationPayload(reservation, now),
            createdAtUtc: now);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inventory released. ReservationId={ReservationId}",
            reservationId);

        return ReleaseInventoryResult.Successful();
    }

    private async Task<ReleaseInventoryResult> ExpireInternalAsync(
        InventoryDbContext dbContext,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var reservation = await LoadReservationWithItemsAsync(dbContext, reservationId, cancellationToken);

        if (reservation is null)
        {
            return ReleaseInventoryResult.Successful(
                isIdempotentReplay: true,
                message: "Reservation not found.");
        }

        if (reservation.Status != ReservationStatus.Active)
        {
            return ReleaseInventoryResult.Successful(
                isIdempotentReplay: true,
                message: $"Reservation already transitioned to {reservation.Status}.");
        }

        var now = DateTime.UtcNow;

        await RestoreStockForReservationAsync(dbContext, reservation, now, cancellationToken);

        reservation.Status = ReservationStatus.Expired;
        reservation.UpdatedAtUtc = now;

        AddOutboxMessage(
            dbContext,
            eventType: "InventoryExpired",
            payload: BuildInventoryReservationPayload(reservation, now),
            createdAtUtc: now);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inventory reservation expired. ReservationId={ReservationId}",
            reservationId);

        return ReleaseInventoryResult.Successful();
    }

    private static Task<InventoryReservationEntity?> LoadReservationWithItemsAsync(
        InventoryDbContext dbContext,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        return dbContext.InventoryReservations
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.ReservationId == reservationId, cancellationToken);
    }

    private static Task<Dictionary<Guid, ProductStockEntity>> LoadStocksByProductIdsAsync(
        InventoryDbContext dbContext,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken)
    {
        return dbContext.ProductStocks
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, cancellationToken);
    }

    private static async Task RestoreStockForReservationAsync(
        InventoryDbContext dbContext,
        InventoryReservationEntity reservation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var productIds = reservation.Items.Select(x => x.ProductId).Distinct().ToList();
        var stocks = await LoadStocksByProductIdsAsync(dbContext, productIds, cancellationToken);

        foreach (var item in reservation.Items)
        {
            if (!stocks.TryGetValue(item.ProductId, out var stock))
                continue;

            stock.AvailableQuantity += item.Quantity;
            stock.ReservedQuantity = Math.Max(0, stock.ReservedQuantity - item.Quantity);
            stock.UpdatedAtUtc = now;

            AddInventoryMovement(
                dbContext,
                item.ProductId,
                MovementType.Release,
                item.Quantity,
                reservation.ReservationId.ToString(),
                now);
        }
    }

    private static void AddInventoryMovement(
        InventoryDbContext dbContext,
        Guid productId,
        string movementType,
        int quantityDelta,
        string correlationId,
        DateTime createdAtUtc)
    {
        dbContext.InventoryMovements.Add(new InventoryMovementEntity
        {
            MovementId = Guid.NewGuid(),
            ProductId = productId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            CorrelationId = correlationId,
            CreatedAtUtc = createdAtUtc
        });
    }

    private void AddOutboxMessage(
        InventoryDbContext dbContext,
        string eventType,
        object payload,
        DateTime createdAtUtc)
    {
        dbContext.OutboxMessages.Add(new OutboxMessageEntity
        {
            OutboxMessageId = Guid.NewGuid(),
            Topic = inventoryEventsTopic,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            CreatedAtUtc = createdAtUtc,
            RetryCount = 0,
            LastError = string.Empty
        });
    }

    private static object BuildInventoryReservedPayload(
        Guid reservationId,
        Guid orderId,
        IReadOnlyCollection<ReserveStockItem> items,
        DateTime occurredAtUtc)
    {
        return new
        {
            reservationId = reservationId.ToString(),
            orderId = orderId.ToString(),
            items = items.Select(x => new
            {
                productId = x.ProductId.ToString(),
                quantity = x.Quantity
            }),
            occurredAtUtc
        };
    }

    private static object BuildInventoryReservationPayload(
        InventoryReservationEntity reservation,
        DateTime occurredAtUtc)
    {
        return new
        {
            reservationId = reservation.ReservationId.ToString(),
            orderId = reservation.OrderId.ToString(),
            status = reservation.Status,
            items = reservation.Items.Select(x => new
            {
                productId = x.ProductId.ToString(),
                quantity = x.Quantity
            }),
            occurredAtUtc
        };
    }
}
