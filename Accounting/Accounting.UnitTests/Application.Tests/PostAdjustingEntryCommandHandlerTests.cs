using Application.Commands.PostAdjustingEntry;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests;

public class PostAdjustingEntryCommandHandlerTests
{
    private readonly ILedgerTransactionRepository _repository =
        Substitute.For<ILedgerTransactionRepository>();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly PostAdjustingEntryCommandHandler _handler;

    public PostAdjustingEntryCommandHandlerTests()
    {
        _handler = new PostAdjustingEntryCommandHandler(
            _repository,
            _unitOfWork,
            NullLogger<PostAdjustingEntryCommandHandler>.Instance);
    }

    private static PostAdjustingEntryCommand Command(
        IReadOnlyList<AdjustingEntryLeg>? legs = null,
        string reason = "correcting a mis-post",
        string postedBy = "ops@example.test",
        string adjustmentId = "adj-1") =>
        new(
            adjustmentId,
            "USD",
            legs ??
            [
                new AdjustingEntryLeg(LedgerAccount.CustomerCaptured, EntryDirection.Debit, 10m),
                new AdjustingEntryLeg(LedgerAccount.MerchantRevenue, EntryDirection.Credit, 10m),
            ],
            reason,
            postedBy,
            null);

    [Fact]
    public async Task Handle_ShouldPostABalancedAdjustment_AndRecordWhoAndWhy()
    {
        LedgerTransaction? posted = null;
        _repository
            .When(r => r.AddAsync(Arg.Any<LedgerTransaction>(), Arg.Any<CancellationToken>()))
            .Do(call => posted = call.Arg<LedgerTransaction>());

        var result = await _handler.Handle(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("adjustment:adj-1", posted!.TransactionRef);
        Assert.Equal(TransactionRefType.Adjustment, posted.RefType);
        Assert.Equal("correcting a mis-post", posted.Reason);
        Assert.Equal("ops@example.test", posted.PostedBy);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldRejectAnUnbalancedAdjustment()
    {
        var result = await _handler.Handle(
            Command(legs:
            [
                new AdjustingEntryLeg(LedgerAccount.CustomerCaptured, EntryDirection.Debit, 10m),
                new AdjustingEntryLeg(LedgerAccount.MerchantRevenue, EntryDirection.Credit, 7m),
            ]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_ShouldRequireAReason(string reason)
    {
        var result = await _handler.Handle(Command(reason: reason), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("reason", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_ShouldRequireAtLeastTwoLegs()
    {
        var result = await _handler.Handle(
            Command(legs: [new AdjustingEntryLeg(LedgerAccount.CustomerCaptured, EntryDirection.Debit, 10m)]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_ShouldRejectANonPositiveLeg()
    {
        var result = await _handler.Handle(
            Command(legs:
            [
                new AdjustingEntryLeg(LedgerAccount.CustomerCaptured, EntryDirection.Debit, 0m),
                new AdjustingEntryLeg(LedgerAccount.MerchantRevenue, EntryDirection.Credit, 0m),
            ]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_ShouldBeIdempotentOnTheAdjustmentId()
    {
        // A double submit from the console must not post the correction twice.
        var existing = LedgerTransaction.ForManualAdjustment(
            "adj-1",
            "USD",
            [
                new AdjustmentLeg(LedgerAccount.CustomerCaptured, EntryDirection.Debit, 10m),
                new AdjustmentLeg(LedgerAccount.MerchantRevenue, EntryDirection.Credit, 10m),
            ],
            "correcting a mis-post",
            "ops@example.test",
            null,
            DateTime.UtcNow);

        _repository.GetByTransactionRefAsync("adjustment:adj-1", Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id.ToString(), result.Value);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }
}
