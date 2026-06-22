using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Models;
using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.OrderSaga.Steps;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Domain.Entities.Order;
using Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Order.IntegrationTests.Infrastructure;
using System.Text.Json;
using Xunit;
using OrderAggregate = Domain.Entities.Order.Order;

namespace Order.IntegrationTests.Sagas;

[Collection("Integration")]
public sealed class OrderSagaCompensationFlowTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public OrderSagaCompensationFlowTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExecuteAsync_ShouldCompensateInReverseOrder_AndCancelOrder_WithRealSagaRepository()
    {
        await using var scope = _fixture.CreateScope();

        var sagaRepository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();
        var realOrderPersistence = scope.ServiceProvider.GetRequiredService<IOrderPersistenceService>();

        var (order, data) = BuildPendingOrderAndData();
        await realOrderPersistence.CreateOrderAsync(order, $"idem-{Guid.NewGuid()}", CancellationToken.None);

        var compensationSequence = new List<string>();
        var orderPersistence = new RecordingOrderPersistenceService(realOrderPersistence, compensationSequence);

        var steps = new ISagaStep<OrderSagaData, OrderSagaContext>[]
        {
            new CancelOrderOnFailureStep(
                orderPersistence,
                new NoopPaymentGateway(),
                new NoopIncidentReporter(),
                NullLogger<CancelOrderOnFailureStep>.Instance),
            new RecordingSuccessfulStep("ReserveInventory", 1, compensationSequence),
            new RecordingSuccessfulStep("CreateShipment", 2, compensationSequence),
            new FailingStep("ProcessPayment", 3, "Payment was declined")
        };

        var saga = new Application.Sagas.OrderSaga.OrderSaga(
            sagaRepository,
            steps,
            new NonTransientErrorClassifier(),
            NullLogger<Application.Sagas.OrderSaga.OrderSaga>.Instance);

        var result = await saga.ExecuteAsync(data, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(SagaStatus.Failed);

        compensationSequence.Should().Equal(
            "CreateShipment.Compensate",
            "ReserveInventory.Compensate",
            "CancelOrderOnFailure.Compensate");

        var persistedSaga = await sagaRepository.GetByCorrelationIdAsync(
            data.CorrelationId,
            "OrderSaga",
            CancellationToken.None);

        persistedSaga.Should().NotBeNull();
        persistedSaga!.Status.Should().Be(SagaStatus.Compensated);

        var stepLogs = await sagaRepository.GetStepLogsAsync(persistedSaga.Id, CancellationToken.None);

        stepLogs.Single(s => s.StepName == "CreateShipment").Status.Should().Be(StepStatus.Compensated);
        stepLogs.Single(s => s.StepName == "ReserveInventory").Status.Should().Be(StepStatus.Compensated);
        stepLogs.Single(s => s.StepName == "ProcessPayment").Status.Should().Be(StepStatus.Failed);
        stepLogs.Single(s => s.StepName == "CancelOrderOnFailure").Status.Should().Be(StepStatus.Compensated);

        var reloadedOrder = await realOrderPersistence.LoadOrderAsync(data.CorrelationId, CancellationToken.None);
        reloadedOrder.Should().NotBeNull();
        reloadedOrder!.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPauseSaga_WhenPaymentGatewayTimesOut()
    {
        await using var scope = _fixture.CreateScope();

        var sagaRepository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();

        var (_, data) = BuildPendingOrderAndData();

        var steps = new ISagaStep<OrderSagaData, OrderSagaContext>[]
        {
            new CapturePaymentStep(
                new TimeoutPaymentGateway(),
                new NoopCompensationRefundRetryRepository(),
                new NoopIncidentReporter(),
                NullLogger<CapturePaymentStep>.Instance)
        };

        var saga = new Application.Sagas.OrderSaga.OrderSaga(
            sagaRepository,
            steps,
            new NonTransientErrorClassifier(),
            NullLogger<Application.Sagas.OrderSaga.OrderSaga>.Instance);

        var result = await saga.ExecuteAsync(data, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(SagaStatus.Completed);

        var persistedSaga = await sagaRepository.GetByCorrelationIdAsync(
            data.CorrelationId,
            "OrderSaga",
            CancellationToken.None);

        persistedSaga.Should().NotBeNull();
        persistedSaga!.Status.Should().Be(SagaStatus.WaitingForEvent);

        var persistedContext = JsonSerializer.Deserialize<OrderSagaContext>(persistedSaga.Context);
        persistedContext.Should().NotBeNull();
        persistedContext!.PaymentStatus.Should().Be(OrderSagaPaymentStatus.Uncertain);

        var stepLogs = await sagaRepository.GetStepLogsAsync(persistedSaga.Id, CancellationToken.None);
        stepLogs.Single(s => s.StepName == "ProcessPayment").Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCompensate_WhenPaymentServiceIsUnavailable()
    {
        await using var scope = _fixture.CreateScope();

        var sagaRepository = scope.ServiceProvider.GetRequiredService<ISagaRepository>();
        var compensationSequence = new List<string>();

        var (_, data) = BuildPendingOrderAndData();

        var steps = new ISagaStep<OrderSagaData, OrderSagaContext>[]
        {
            new RecordingSuccessfulStep("ReserveInventory", 1, compensationSequence),
            new CapturePaymentStep(
                new UnavailablePaymentGateway(),
                new NoopCompensationRefundRetryRepository(),
                new NoopIncidentReporter(),
                NullLogger<CapturePaymentStep>.Instance)
        };

        var saga = new Application.Sagas.OrderSaga.OrderSaga(
            sagaRepository,
            steps,
            new NonTransientErrorClassifier(),
            NullLogger<Application.Sagas.OrderSaga.OrderSaga>.Instance);

        var result = await saga.ExecuteAsync(data, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(SagaStatus.Failed);

        compensationSequence.Should().Equal("ReserveInventory.Compensate");

        var persistedSaga = await sagaRepository.GetByCorrelationIdAsync(
            data.CorrelationId,
            "OrderSaga",
            CancellationToken.None);

        persistedSaga.Should().NotBeNull();
        persistedSaga!.Status.Should().Be(SagaStatus.Compensated);

        var persistedContext = JsonSerializer.Deserialize<OrderSagaContext>(persistedSaga.Context);
        persistedContext.Should().NotBeNull();
        persistedContext!.PaymentStatus.Should().Be(OrderSagaPaymentStatus.Failed);
        persistedContext.PaymentFailureCode.Should().Be("PAYMENT_GATEWAY_UNAVAILABLE");

        var stepLogs = await sagaRepository.GetStepLogsAsync(persistedSaga.Id, CancellationToken.None);
        stepLogs.Single(s => s.StepName == "ProcessPayment").Status.Should().Be(StepStatus.Failed);
        stepLogs.Single(s => s.StepName == "ReserveInventory").Status.Should().Be(StepStatus.Compensated);
    }

    private static (OrderAggregate Order, OrderSagaData Data) BuildPendingOrderAndData()
    {
        var customerId = CustomerId.CreateUnique();

        var order = OrderAggregate.Create(
            customerId,
            Address.Create("Baker St", "London", "UK", "NW1"),
            new List<OrderItem>
            {
                OrderItem.Create(ProductId.CreateUnique(), 1, Money.Create(25m, "USD"))
            },
            "CreditCard");

        var data = new OrderSagaData
        {
            CorrelationId = order.Id.Value,
            CustomerId = customerId.Value,
            TotalAmount = 25m,
            Currency = "USD",
            PaymentMethod = Application.Common.Enums.PaymentMethod.CreditCard,
            DeliveryAddress = new AddressDto("Baker St", "London", "UK", "NW1"),
            Items =
            [
                new OrderItemDto(Guid.NewGuid(), 1, 25m, "USD")
            ]
        };

        return (order, data);
    }

    private sealed class RecordingSuccessfulStep(
        string stepName,
        int order,
        List<string> compensationSequence)
        : ISagaStep<OrderSagaData, OrderSagaContext>
    {
        public string StepName => stepName;
        public int Order => order;

        public Task<StepOutcome> ExecuteAsync(
            OrderSagaData data,
            OrderSagaContext context,
            CancellationToken cancellationToken)
            => Task.FromResult<StepOutcome>(new Completed());

        public Task CompensateAsync(
            OrderSagaData data,
            OrderSagaContext context,
            CancellationToken cancellationToken)
        {
            compensationSequence.Add($"{StepName}.Compensate");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStep(string stepName, int order, string error)
        : ISagaStep<OrderSagaData, OrderSagaContext>
    {
        public string StepName => stepName;
        public int Order => order;

        public Task<StepOutcome> ExecuteAsync(
            OrderSagaData data,
            OrderSagaContext context,
            CancellationToken cancellationToken)
            => Task.FromResult<StepOutcome>(new Fail(error));

        public Task CompensateAsync(
            OrderSagaData data,
            OrderSagaContext context,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingOrderPersistenceService(
        IOrderPersistenceService inner,
        List<string> compensationSequence)
        : IOrderPersistenceService
    {
        public Task CreateOrderAsync(
            OrderAggregate order,
            string idempotencyKey,
            CancellationToken cancellationToken)
            => inner.CreateOrderAsync(order, idempotencyKey, cancellationToken);

        public async Task UpdateOrderAsync(
            Guid orderId,
            Func<OrderAggregate, Task> action,
            CancellationToken ct)
        {
            compensationSequence.Add("CancelOrderOnFailure.Compensate");
            await inner.UpdateOrderAsync(orderId, action, ct);
        }

        public Task<OrderAggregate?> LoadOrderAsync(Guid orderId, CancellationToken ct)
            => inner.LoadOrderAsync(orderId, ct);
    }

    private sealed class NonTransientErrorClassifier : ISagaErrorClassifier
    {
        public bool IsTransient(Exception ex) => false;
    }

    private sealed class NoopIncidentReporter : IIncidentReporter
    {
        public Task SendAlertAsync(IncidentAlert alert, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task CreateInterventionTicketAsync(InterventionTicket ticket, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TimeoutPaymentGateway : IPaymentGateway
    {
        public Task<PaymentProcessingResult> ProcessPaymentAsync(
            Guid orderId,
            Guid customerId,
            decimal amount,
            string currency,
            string paymentMethod,
            CancellationToken cancellationToken)
            => throw new GatewayUnavailableException(
                GatewayUnavailableReason.Timeout,
                "simulated deadline exceeded");

        public Task<PaymentProcessingResult> AuthorizeAsync(
            Guid orderId, Guid customerId, decimal amount, string currency,
            string paymentMethod, CancellationToken cancellationToken)
            => throw new GatewayUnavailableException(
                GatewayUnavailableReason.Timeout,
                "simulated deadline exceeded");

        public Task<PaymentProcessingResult> CaptureAsync(
            Guid orderId, Guid customerId, string providerPaymentIntentId,
            decimal amount, string currency, CancellationToken cancellationToken)
            => throw new GatewayUnavailableException(
                GatewayUnavailableReason.Timeout,
                "simulated deadline exceeded");

        public Task CancelAuthorizationAsync(string providerPaymentIntentId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string> RefundAsync(
            string paymentId,
            decimal amount,
            string currency,
            string reason,
            CancellationToken cancellationToken)
            => Task.FromResult("REF-NOT-USED");

        public Task<RefundProcessingResult> RefundWithStatusAsync(
            string paymentId,
            decimal amount,
            string currency,
            string reason,
            CancellationToken cancellationToken)
            => Task.FromResult(new RefundProcessingResult("REF-NOT-USED", RefundProcessingStatus.Succeeded));
    }

    private sealed class UnavailablePaymentGateway : IPaymentGateway
    {
        public Task<PaymentProcessingResult> ProcessPaymentAsync(
            Guid orderId,
            Guid customerId,
            decimal amount,
            string currency,
            string paymentMethod,
            CancellationToken cancellationToken)
            => throw new GatewayUnavailableException(
                GatewayUnavailableReason.ServiceUnavailable,
                "simulated service unavailable");

        public Task<PaymentProcessingResult> AuthorizeAsync(
            Guid orderId, Guid customerId, decimal amount, string currency,
            string paymentMethod, CancellationToken cancellationToken)
            => throw new GatewayUnavailableException(
                GatewayUnavailableReason.ServiceUnavailable,
                "simulated service unavailable");

        public Task<PaymentProcessingResult> CaptureAsync(
            Guid orderId, Guid customerId, string providerPaymentIntentId,
            decimal amount, string currency, CancellationToken cancellationToken)
            => throw new GatewayUnavailableException(
                GatewayUnavailableReason.ServiceUnavailable,
                "simulated service unavailable");

        public Task CancelAuthorizationAsync(string providerPaymentIntentId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string> RefundAsync(
            string paymentId,
            decimal amount,
            string currency,
            string reason,
            CancellationToken cancellationToken)
            => Task.FromResult("REF-NOT-USED");

        public Task<RefundProcessingResult> RefundWithStatusAsync(
            string paymentId,
            decimal amount,
            string currency,
            string reason,
            CancellationToken cancellationToken)
            => Task.FromResult(new RefundProcessingResult("REF-NOT-USED", RefundProcessingStatus.Succeeded));
    }

    private sealed class NoopCompensationRefundRetryRepository : ICompensationRefundRetryRepository
    {
        public Task<CompensationRefundRetry> EnqueueIfNotExistsAsync(
            Guid orderId,
            string paymentId,
            decimal amount,
            string currency,
            string reason,
            CancellationToken cancellationToken)
            => Task.FromResult(CompensationRefundRetry.Create(orderId, paymentId, amount, currency, reason));

        public Task<CompensationRefundRetry?> GetPendingByOrderAndPaymentAsync(
            Guid orderId,
            string paymentId,
            CancellationToken cancellationToken)
            => Task.FromResult<CompensationRefundRetry?>(null);

        public Task<IReadOnlyList<CompensationRefundRetry>> GetDuePendingAsync(
            DateTime nowUtc,
            int batchSize,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CompensationRefundRetry>>(new List<CompensationRefundRetry>());

        public Task<IReadOnlyList<CompensationRefundRetry>> ClaimDuePendingAsync(
            DateTime nowUtc,
            int batchSize,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CompensationRefundRetry>>(new List<CompensationRefundRetry>());

        public Task SaveAsync(CompensationRefundRetry retry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class NoopPaymentGateway : IPaymentGateway
    {
        public Task<PaymentProcessingResult> ProcessPaymentAsync(
            Guid orderId, Guid customerId, decimal amount, string currency,
            string paymentMethod, CancellationToken cancellationToken)
            => Task.FromResult(new PaymentProcessingResult("PAY-NOOP", PaymentProcessingStatus.Succeeded));

        public Task<PaymentProcessingResult> AuthorizeAsync(
            Guid orderId, Guid customerId, decimal amount, string currency,
            string paymentMethod, CancellationToken cancellationToken)
            => Task.FromResult(new PaymentProcessingResult("PAY-NOOP", PaymentProcessingStatus.Authorized, "pi_noop"));

        public Task<PaymentProcessingResult> CaptureAsync(
            Guid orderId, Guid customerId, string providerPaymentIntentId,
            decimal amount, string currency, CancellationToken cancellationToken)
            => Task.FromResult(new PaymentProcessingResult("PAY-NOOP", PaymentProcessingStatus.Succeeded));

        public Task CancelAuthorizationAsync(string providerPaymentIntentId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string> RefundAsync(string paymentId, decimal amount, string currency,
            string reason, CancellationToken cancellationToken)
            => Task.FromResult("REF-NOOP");

        public Task<RefundProcessingResult> RefundWithStatusAsync(string paymentId, decimal amount,
            string currency, string reason, CancellationToken cancellationToken)
            => Task.FromResult(new RefundProcessingResult("REF-NOOP", RefundProcessingStatus.Succeeded));
    }
}