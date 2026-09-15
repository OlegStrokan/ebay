using Api.GrpcServices;
using Application.Commands.CancelReversal;
using Application.Commands.ReverseRevenue;
using Application.Common;
using Grpc.Core;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Protos.Accounting;
using Protos.Common;

namespace Api.Tests;

// Order's AccountingGateway reads Success, then ReversalId, then ErrorMessage. Nothing at compile
// time ties that gateway to this server, so these assertions are the contract between them.
public class AccountingGrpcServiceContractTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly AccountingGrpcService _service;

    public AccountingGrpcServiceContractTests()
    {
        _service = new AccountingGrpcService(_mediator, NullLogger<AccountingGrpcService>.Instance);
    }

    private static ServerCallContext Context() => TestServerCallContext.Create();

    private static ReverseRevenueRequest ReverseRequest(string? orderId = null, string? returnRequestId = null) =>
        new()
        {
            OrderId = orderId ?? Guid.NewGuid().ToString(),
            ReturnRequestId = returnRequestId ?? Guid.NewGuid().ToString(),
            Amount = new DecimalValue { Units = 100, Nanos = 0 },
            Currency = "EUR",
        };

    [Fact]
    public async Task ReverseRevenue_ShouldReturnSuccessAndReversalId_WhenHandlerSucceeds()
    {
        _mediator.Send(Arg.Any<ReverseRevenueCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("rev-123"));

        var response = await _service.ReverseRevenue(ReverseRequest(), Context());

        Assert.True(response.Success);
        Assert.Equal("rev-123", response.ReversalId);
    }

    [Fact]
    public async Task ReverseRevenue_ShouldReturnFailureWithAMessage_WhenHandlerFails()
    {
        // Order surfaces ErrorMessage verbatim in the exception it throws, so an empty
        // message would leave the saga failure unexplained.
        _mediator.Send(Arg.Any<ReverseRevenueCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure("period is closed"));

        var response = await _service.ReverseRevenue(ReverseRequest(), Context());

        Assert.False(response.Success);
        Assert.Equal("period is closed", response.ErrorMessage);
        Assert.Empty(response.ReversalId);
    }

    [Theory]
    [InlineData("not-a-guid", null)]
    [InlineData(null, "not-a-guid")]
    [InlineData(null, "00000000-0000-0000-0000-000000000000")]
    public async Task ReverseRevenue_ShouldRejectBadIds_WithoutCallingTheHandler(
        string? orderId,
        string? returnRequestId)
    {
        var response = await _service.ReverseRevenue(ReverseRequest(orderId, returnRequestId), Context());

        Assert.False(response.Success);
        Assert.NotEmpty(response.ErrorMessage);

        await _mediator.DidNotReceive().Send(Arg.Any<ReverseRevenueCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelReversal_ShouldReturnSuccess_WhenHandlerSucceeds()
    {
        _mediator.Send(Arg.Any<CancelReversalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Success(true));

        var response = await _service.CancelReversal(
            new CancelReversalRequest { ReversalId = "rev-123", Reason = "return cancelled" },
            Context());

        Assert.True(response.Success);
    }

    [Fact]
    public async Task CancelReversal_ShouldReturnFailureWithAMessage_WhenHandlerFails()
    {
        _mediator.Send(Arg.Any<CancelReversalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Failure("reversal not found"));

        var response = await _service.CancelReversal(
            new CancelReversalRequest { ReversalId = "rev-missing", Reason = "return cancelled" },
            Context());

        Assert.False(response.Success);
        Assert.Equal("reversal not found", response.ErrorMessage);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders = [];
        private readonly Metadata _responseTrailers = [];

        public static TestServerCallContext Create() => new();

        protected override string MethodCore => "test";
        protected override string HostCore => "test";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
