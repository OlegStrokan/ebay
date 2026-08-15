using Application.Commands.RecordRefund;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Application.Tests;

public class RecordRefundCommandHandlerTests
{
    private readonly ILedgerTransactionRepository _repository = Substitute.For<ILedgerTransactionRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<RecordRefundCommandHandler> _logger =
        NullLogger<RecordRefundCommandHandler>.Instance;
    private readonly RecordRefundCommandHandler _handler;

    public RecordRefundCommandHandlerTests()
    {
        _handler = new RecordRefundCommandHandler(_repository, _unitOfWork, _logger);
    }

    [Fact]
    public async Task Handle_ShouldPostNewTransaction_WhenRefundNotSeenBefore()
    {
        var command = new RecordRefundCommand(Guid.NewGuid(), "REF-1", 100m, "EUR", "Defective");
        _repository.GetByTransactionRefAsync("refund:REF-1", Arg.Any<CancellationToken>())
            .Returns((LedgerTransaction?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Value));
        await _repository.Received(1).AddAsync(Arg.Any<LedgerTransaction>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldBeIdempotent_WhenTransactionRefAlreadyExists()
    {
        var existing = LedgerTransaction.ForRefund(Guid.NewGuid(), "REF-1", new Money(100m, "EUR"), DateTime.UtcNow);
        var command = new RecordRefundCommand(Guid.NewGuid(), "REF-1", 100m, "EUR", "Defective");
        _repository.GetByTransactionRefAsync("refund:REF-1", Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id.ToString(), result.Value);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Handle_ShouldFail_WhenRefundIdMissing()
    {
        var command = new RecordRefundCommand(Guid.NewGuid(), "", 100m, "EUR", "Defective");

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Handle_ShouldReturnExistingTransaction_WhenSaveRacesWithConcurrentInsert()
    {
        // Two requests for the same RefundId both pass the "not seen before" check under
        // Read Committed; the loser's SaveChangesAsync hits the unique constraint on
        // TransactionRef and EfUnitOfWork surfaces that as DuplicateLedgerTransactionException.
        // The handler must collapse that into the winner's transaction instead of failing.
        var winner = LedgerTransaction.ForRefund(Guid.NewGuid(), "REF-1", new Money(100m, "EUR"), DateTime.UtcNow);
        var command = new RecordRefundCommand(Guid.NewGuid(), "REF-1", 100m, "EUR", "Defective");

        _repository.GetByTransactionRefAsync("refund:REF-1", Arg.Any<CancellationToken>())
            .Returns((LedgerTransaction?)null, winner);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new DuplicateLedgerTransactionException("refund:REF-1"));

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(winner.Id.ToString(), result.Value);
    }
}
