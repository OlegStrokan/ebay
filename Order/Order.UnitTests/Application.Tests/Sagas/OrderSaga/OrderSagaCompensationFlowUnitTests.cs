using System.Text.Json;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Application.Tests.Sagas.OrderSaga;

public class OrderSagaCompensationFlowUnitTests
{
    [Fact]
    public async Task CompensateAsync_ShouldRunCompletedOrderSagaSteps_InReverseOrder()
    {
        var sagaRepository = Substitute.For<ISagaRepository>();
        var errorClassifier = Substitute.For<ISagaErrorClassifier>();
        var logger = Substitute.For<ILogger<Application.Sagas.OrderSaga.OrderSaga>>();

        errorClassifier.IsTransient(Arg.Any<Exception>()).Returns(false);

        var cancelOrderStep = BuildStep("CancelOrderOnFailure", 0);
        var reserveInventoryStep = BuildStep("ReserveInventory", 1);
        var createShipmentStep = BuildStep("CreateShipment", 2);
        var processPaymentStep = BuildStep("ProcessPayment", 3);

        var sagaId = Guid.NewGuid();

        var data = new OrderSagaData
        {
            CorrelationId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            TotalAmount = 50m,
            Currency = "USD",
            PaymentMethod = Application.Common.Enums.PaymentMethod.CreditCard,
            DeliveryAddress = new AddressDto("Baker St", "London", "UK", "NW1"),
            Items =
            [
                new OrderItemDto(Guid.NewGuid(), 1, 50m, "USD")
            ]
        };

        sagaRepository
            .GetByIdAsync(sagaId, Arg.Any<CancellationToken>())
            .Returns(new SagaState
            {
                Id = sagaId,
                CorrelationId = data.CorrelationId,
                Status = SagaStatus.Failed,
                SagaType = "OrderSaga",
                Payload = JsonSerializer.Serialize(data),
                Context = JsonSerializer.Serialize(new OrderSagaContext()),
                Steps =
                [
                    new SagaStepLog { StepName = "CancelOrderOnFailure", Status = StepStatus.Completed },
                    new SagaStepLog { StepName = "ReserveInventory", Status = StepStatus.Completed },
                    new SagaStepLog { StepName = "CreateShipment", Status = StepStatus.Completed },
                    new SagaStepLog { StepName = "ProcessPayment", Status = StepStatus.Failed }
                ]
            });

        var saga = new Application.Sagas.OrderSaga.OrderSaga(
            sagaRepository,
            [cancelOrderStep, reserveInventoryStep, createShipmentStep, processPaymentStep],
            errorClassifier,
            logger,
            Substitute.For<IFailedCompensationRetryRepository>(),
            Substitute.For<IIncidentReporter>());

        var result = await saga.CompensateAsync(sagaId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SagaStatus.Compensated, result.Status);

        Received.InOrder(() =>
        {
            createShipmentStep.CompensateAsync(
                Arg.Any<OrderSagaData>(),
                Arg.Any<OrderSagaContext>(),
                Arg.Any<CancellationToken>());

            reserveInventoryStep.CompensateAsync(
                Arg.Any<OrderSagaData>(),
                Arg.Any<OrderSagaContext>(),
                Arg.Any<CancellationToken>());

            cancelOrderStep.CompensateAsync(
                Arg.Any<OrderSagaData>(),
                Arg.Any<OrderSagaContext>(),
                Arg.Any<CancellationToken>());
        });

        await processPaymentStep.DidNotReceive().CompensateAsync(
            Arg.Any<OrderSagaData>(),
            Arg.Any<OrderSagaContext>(),
            Arg.Any<CancellationToken>());
    }

    private static ISagaStep<OrderSagaData, OrderSagaContext> BuildStep(string stepName, int order)
    {
        var step = Substitute.For<ISagaStep<OrderSagaData, OrderSagaContext>>();
        step.StepName.Returns(stepName);
        step.Order.Returns(order);
        step.CompensateAsync(
            Arg.Any<OrderSagaData>(),
            Arg.Any<OrderSagaContext>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return step;
    }
}