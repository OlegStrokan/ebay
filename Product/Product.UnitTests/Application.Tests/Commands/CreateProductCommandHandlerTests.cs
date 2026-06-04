using Application.Commands.CreateProduct;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Commands;

[TestFixture]
public class CreateProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private CreateProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new CreateProductCommandHandler(_persistence);
    }

    private static CreateProductCommand ValidCommand() => new(
        SellerId: Guid.NewGuid(),
        Name: "Test Product",
        Description: "A description",
        Price: 99.99m,
        Currency: "USD",
        InitialStock: 10,
        Attributes: [new ProductAttributeDto("color", "red")],
        ImageUrls: ["https://example.com/img.jpg"]);

    [Test]
    public async Task Handle_ShouldReturnSuccess_AndCallPersistence()
    {
        var command = ValidCommand();

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.EqualTo(Guid.Empty));
        await _persistence.Received(1).CreateProductAsync(
            Arg.Any<Domain.Entities.Product>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnProductId()
    {
        var command = ValidCommand();

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void Handle_ShouldThrow_WhenPersistenceThrows()
    {
        _persistence
            .CreateProductAsync(Arg.Any<Domain.Entities.Product>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("DB error"));

        // Infrastructure exceptions propagate so the caller can log them with full stack trace.
        Assert.ThrowsAsync<Exception>(() => _handler.Handle(ValidCommand(), CancellationToken.None));
    }

    [Test]
    public async Task Handle_WithEmptyName_ShouldReturnDomainFailure()
    {
        var command = ValidCommand() with { Name = "" };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("name"));
        await _persistence.DidNotReceive().CreateProductAsync(
            Arg.Any<Domain.Entities.Product>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithNegativeStock_ShouldReturnDomainFailure()
    {
        var command = ValidCommand() with { InitialStock = -1 };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        await _persistence.DidNotReceive().CreateProductAsync(
            Arg.Any<Domain.Entities.Product>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithEmptySellerId_ShouldReturnFailure()
    {
        var command = ValidCommand() with { SellerId = Guid.Empty };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public async Task Handle_WithNullAttributes_ShouldSucceed()
    {
        var command = ValidCommand() with { Attributes = [] };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }
}
