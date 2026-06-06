using Api.GrpcServices;
using Application.Commands.ProcessPayment;
using Application.Commands.RefundPayment;
using Application.Common;
using Application.DTOs;
using Application.Queries.GetPaymentById;
using Domain.Enums;
using Grpc.Core;
using MediatR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Protos.Common;
using Protos.Payment;

namespace Api.Tests;

public class PaymentGrpcServiceTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<PaymentGrpcService> _logger = Substitute.For<ILogger<PaymentGrpcService>>();
    private readonly ServerCallContext _callContext = Substitute.For<ServerCallContext>();

    private PaymentGrpcService BuildService() =>
        new(_mediator, _logger);

    [Fact]
    public async Task ProcessPayment_ShouldMapDefaults_AndPassCommandToMediator()
    {
        var request = new ProcessPaymentRequest
        {
            OrderId = "order-1",
            CustomerId = "customer-1",
            Amount = new DecimalValue { Units = 12, Nanos = 340000000 },
            Currency = "",
            PaymentMethod = "card",
            IdempotencyKey = "",
            CustomerEmail = " test@example.test "
        };

        _mediator
            .Send(Arg.Any<ProcessPaymentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProcessPaymentResultDto>.Success(
                new ProcessPaymentResultDto(
                    PaymentId: "pay-1",
                    Status: Application.DTOs.ProcessPaymentStatus.Pending,
                    ProviderPaymentIntentId: "pi_123",
                    ClientSecret: "cs_123",
                    ErrorCode: null,
                    ErrorMessage: null)));

        var response = await BuildService().ProcessPayment(request, _callContext);

        Assert.True(response.Success);
        Assert.Equal(Protos.Payment.ProcessPaymentStatus.Pending, response.Status);
        Assert.Equal("pay-1", response.PaymentId);

        await _mediator.Received(1).Send(
            Arg.Is<ProcessPaymentCommand>(cmd =>
                cmd.OrderId == "order-1" &&
                cmd.CustomerId == "customer-1" &&
                cmd.Amount == 12.34m &&
                cmd.Currency == "USD" &&
                cmd.PaymentMethod == PaymentMethod.Card &&
                cmd.IdempotencyKey.StartsWith("grpc-process:", StringComparison.Ordinal) &&
                cmd.CustomerEmail == "test@example.test"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPayment_ShouldReturnFailure_WhenCommandFails()
    {
        var request = new ProcessPaymentRequest
        {
            OrderId = "order-2",
            CustomerId = "customer-2",
            Amount = new DecimalValue { Units = 10, Nanos = 0 },
            Currency = "EUR",
            PaymentMethod = "card",
            IdempotencyKey = "idem-1"
        };

        _mediator
            .Send(Arg.Any<ProcessPaymentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ProcessPaymentResultDto>.Failure("provider timeout"));

        var response = await BuildService().ProcessPayment(request, _callContext);

        Assert.False(response.Success);
        Assert.Equal(Protos.Payment.ProcessPaymentStatus.Failed, response.Status);
        Assert.Equal("PROCESS_PAYMENT_FAILED", response.ErrorCode);
        Assert.Contains("provider timeout", response.ErrorMessage);
    }

    [Fact]
    public async Task RefundPayment_ShouldResolveCurrencyFromPayment_WhenCurrencyMissing()
    {
        var request = new RefundPaymentRequest
        {
            PaymentId = "pay-42",
            Amount = new DecimalValue { Units = 5, Nanos = 500000000 },
            Currency = "",
            Reason = "requested_by_customer",
            IdempotencyKey = ""
        };

        var paymentDetails = new PaymentDetailsDto(
            PaymentId: "pay-42",
            OrderId: "order-42",
            CustomerId: "customer-42",
            Amount: 20.0m,
            Currency: "eur",
            PaymentMethod: PaymentMethod.Card,
            Status: PaymentStatus.Succeeded,
            ProviderPaymentIntentId: "pi_42",
            ProviderRefundId: null,
            FailureCode: null,
            FailureMessage: null,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            SucceededAt: DateTime.UtcNow,
            FailedAt: null);

        _mediator
            .Send(Arg.Any<GetPaymentByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<PaymentDetailsDto>.Success(paymentDetails));

        _mediator
            .Send(Arg.Any<RefundPaymentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<RefundPaymentResultDto>.Success(
                new RefundPaymentResultDto(
                    PaymentId: "pay-42",
                    RefundId: "ref-42",
                    Status: Application.DTOs.RefundPaymentStatus.Pending,
                    ProviderRefundId: "re_42",
                    ErrorCode: null,
                    ErrorMessage: null)));

        var response = await BuildService().RefundPayment(request, _callContext);

        Assert.True(response.Success);
        Assert.Equal(Protos.Payment.RefundPaymentStatus.Pending, response.Status);
        Assert.Equal("pay-42", response.PaymentId);
        Assert.Equal("ref-42", response.RefundId);

        await _mediator.Received(1).Send(
            Arg.Is<GetPaymentByIdQuery>(q => q.PaymentId == "pay-42"),
            Arg.Any<CancellationToken>());

        await _mediator.Received(1).Send(
            Arg.Is<RefundPaymentCommand>(cmd =>
                cmd.PaymentId == "pay-42" &&
                cmd.Amount == 5.5m &&
                cmd.Currency == "EUR" &&
                cmd.Reason == "requested_by_customer" &&
                cmd.IdempotencyKey.StartsWith("grpc-refund:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }
}