using System.Text.Json;
using Application.Common.Enums;
using Application.Gateways;
using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Application.Tests.Sagas.OrderSaga;

public class InventoryExpiredEventHandlerTests
{
    private readonly IOrderSaga _saga = Substitute.For<IOrderSaga>();
    private readonly ISagaRepository _sagaRepository = Substitute.For<ISagaRepository>();
    private readonly ISagaDistributedLock _distributedLock = Substitute.For<ISagaDistributedLock>();
    private readonly IIncidentReporter _incidentReporter = Substitute.For<IIncidentReporter>();
    private readonly ILogger<InventoryExpiredEventHandler> _logger =
        Substitute.For<ILogger<InventoryExpiredEventHandler>>();

    public InventoryExpiredEventHandlerTests()
    {
        _saga.LockBudget.Returns(TimeSpan.FromMinutes(8));
        _distributedLock
            .TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<ISagaLockHandle>());
    }

    private InventoryExpiredEventHandler Build() =>
        new(_saga, _sagaRepository, _distributedLock, _incidentReporter, _logger);

    // Inventory serializes an anonymous object, so the wire shape is camelCase - unlike every
    // event Order publishes for itself. If this ever stops binding, the handler silently sees an
    // empty OrderId and does nothing.
    private static string Payload(Guid orderId, string reservationId = "res-1") =>
        JsonSerializer.Serialize(new
        {
            reservationId,
            orderId = orderId.ToString(),
            status = 3,
            occurredAtUtc = DateTime.UtcNow,
        });

    [Fact]
    public async Task HandleAsync_ShouldFailAndCompensateSaga_WhenReservationExpiresWhileParked()
    {
        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        _sagaRepository
            .GetByCorrelationIdAsync(orderId, SagaTypes.OrderSaga, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                CorrelationId = orderId,
                SagaType = SagaTypes.OrderSaga,
                Status = SagaStatus.WaitingForEvent,
                CurrentStep = "AwaitPaymentConfirmation",
                WaitReason = "provider authorization confirmation",
            });

        await Build().HandleAsync(Payload(orderId), CancellationToken.None);

        // The saga cannot fulfil the order any more: the stock it held is back on the shelf.
        // Failing now beats discovering it at ConfirmReservation, after the customer has paid.
        await _sagaRepository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Id == sagaId && s.Status == SagaStatus.Failed),
            Arg.Any<CancellationToken>());

        await _saga.Received(1).CompensateAsync(sagaId, Arg.Any<CancellationToken>());

        await _incidentReporter.Received().SendAlertAsync(
            Arg.Is<IncidentAlert>(a =>
                a.AlertType == "SagaInventoryExpiredWhileWaiting"
                && a.OrderId == orderId
                && a.Severity == AlertSeverity.Critical),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SagaStatus.Completed)]
    [InlineData(SagaStatus.Compensated)]
    [InlineData(SagaStatus.Failed)]
    public async Task HandleAsync_ShouldDoNothing_WhenSagaIsNoLongerInFlight(SagaStatus status)
    {
        var orderId = Guid.NewGuid();

        _sagaRepository
            .GetByCorrelationIdAsync(orderId, SagaTypes.OrderSaga, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = Guid.NewGuid(),
                CorrelationId = orderId,
                SagaType = SagaTypes.OrderSaga,
                Status = status,
            });

        await Build().HandleAsync(Payload(orderId), CancellationToken.None);

        await _saga.DidNotReceive().CompensateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _incidentReporter.DidNotReceive().SendAlertAsync(
            Arg.Any<IncidentAlert>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldNotCompensate_WhenLockIsHeldByAConcurrentResume()
    {
        var orderId = Guid.NewGuid();

        _distributedLock
            .TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((ISagaLockHandle?)null);

        await Build().HandleAsync(Payload(orderId), CancellationToken.None);

        // Must not race an in-flight resume; the watchdog picks the saga up on its wait deadline.
        await _sagaRepository.DidNotReceive().GetByCorrelationIdAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _saga.DidNotReceive().CompensateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldMarkFailedToCompensate_WhenCompensationThrows()
    {
        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        _sagaRepository
            .GetByCorrelationIdAsync(orderId, SagaTypes.OrderSaga, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                CorrelationId = orderId,
                SagaType = SagaTypes.OrderSaga,
                Status = SagaStatus.WaitingForEvent,
            });

        _saga
            .CompensateAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns<Task<SagaResult>>(_ => throw new InvalidOperationException("refund gateway down"));

        await Build().HandleAsync(Payload(orderId), CancellationToken.None);

        await _sagaRepository.Received().SaveAsync(
            Arg.Is<SagaState>(s => s.Status == SagaStatus.FailedToCompensate),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShouldDoNothing_WhenPayloadHasNoUsableOrderId()
    {
        await Build().HandleAsync("""{"reservationId":"res-1","orderId":"not-a-guid"}""", CancellationToken.None);

        await _distributedLock.DidNotReceive().TryAcquireAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _saga.DidNotReceive().CompensateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
