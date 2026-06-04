using Application.Interfaces;
using Application.Models;
using Domain.Entities;
using Domain.Events;
using Domain.ValueObjects;
using FluentAssertions;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Product.IntegrationTests.Infrastructure;
using Xunit;

namespace Product.IntegrationTests.Services;

[Collection("Integration")]
public sealed class ProductPersistenceServiceTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public ProductPersistenceServiceTests(IntegrationFixture fixture) => _fixture = fixture;

    private static Domain.Entities.Product BuildProduct(string name = "Integration Widget") =>
        Domain.Entities.Product.Create(
            SellerId.CreateUnique(),
            name,
            "Integration test product",
            CategoryId.CreateUnique(),
            Money.Create(15.00m, "USD"),
            3,
            [],
            []);

    [Fact]
    public async Task CreateProductAsync_ShouldSaveProduct_AndOutboxMessage_InSameTransaction()
    {
        await using var scope = _fixture.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProductPersistenceService>();
        var db  = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product    = BuildProduct();
        var aggregateId = product.Id.Value.ToString();

        await svc.CreateProductAsync(product);

        var savedProduct = await db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == product.Id);

        var outbox = await db.OutboxMessages
            .Where(m => m.AggregateId == aggregateId)
            .ToListAsync();

        savedProduct.Should().NotBeNull("product must be persisted");
        outbox.Should().HaveCount(1,
            "CreateProductAsync raises exactly one domain event (ProductCreatedEvent) → one outbox row");
        outbox[0].Type.Should().Be(nameof(ProductCreatedEvent));
        outbox[0].ProcessedOn.Should().BeNull("new outbox messages are unprocessed");
    }

    [Fact]
    public async Task CreateProductAsync_ShouldClearDomainEvents_AfterSave()
    {
        await using var scope = _fixture.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProductPersistenceService>();

        var product = BuildProduct();

        product.DomainEvents.Should().HaveCount(1, "Create raises one event");

        await svc.CreateProductAsync(product);

        product.DomainEvents.Should().BeEmpty(
            "ClearDomainEvents must be called inside CreateProductAsync after committing");
    }

    [Fact]
    public async Task UpdateProductAsync_ShouldSaveChanges_AndAddOutboxMessage_InSameTransaction()
    {
        // arrange: create first, then load and activate
        await using var createScope = _fixture.CreateScope();
        var createSvc = createScope.ServiceProvider.GetRequiredService<IProductPersistenceService>();

        var product = BuildProduct("Activatable Product");
        await createSvc.CreateProductAsync(product);
        var productId = product.Id;

        // act: open a new scope to simulate a fresh request
        await using var updateScope = _fixture.CreateScope();
        var updateSvc = updateScope.ServiceProvider.GetRequiredService<IProductPersistenceService>();
        var updateDb  = updateScope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var loaded = await updateSvc.GetByIdAsync(productId);
        loaded!.Activate();

        await updateSvc.UpdateProductAsync(loaded);

        // assert: product status persisted, new outbox message added
        var updatedProduct = await updateDb.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId);

        var outboxMessages = await updateDb.OutboxMessages
            .Where(m => m.AggregateId == productId.Value.ToString())
            .ToListAsync();

        updatedProduct!.Status.Should().Be(ProductStatus.Approved,
            "Activate() must be reflected in the database");

        outboxMessages.Should().HaveCount(2,
            "ProductCreatedEvent + ProductStatusChangedEvent = 2 outbox rows");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnPersistedProduct()
    {
        await using var scope = _fixture.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProductPersistenceService>();

        var product = BuildProduct("Readable Product");
        await svc.CreateProductAsync(product);

        var loaded = await svc.GetByIdAsync(product.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Readable Product");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_ForUnknownProduct()
    {
        await using var scope = _fixture.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProductPersistenceService>();

        var result = await svc.GetByIdAsync(ProductId.CreateUnique());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProductAsync_AfterAdjustStock_ShouldPersistNewQuantity_AndAddOutboxMessage()
    {
        await using var createScope = _fixture.CreateScope();
        var createSvc = createScope.ServiceProvider.GetRequiredService<IProductPersistenceService>();

        var product = BuildProduct("Stock-Adjust Product");
        await createSvc.CreateProductAsync(product);
        var productId = product.Id;

        await using var adjustScope = _fixture.CreateScope();
        var adjustSvc = adjustScope.ServiceProvider.GetRequiredService<IProductPersistenceService>();
        var adjustDb  = adjustScope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var loaded = await adjustSvc.GetByIdAsync(productId);
        loaded!.AdjustStock(-2);
        await adjustSvc.UpdateProductAsync(loaded);

        var saved = await adjustDb.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId);

        saved.Should().NotBeNull();
        saved!.StockQuantity.Should().Be(1, "initial stock is 3, delta is -2 → clamped to 1");

        var outbox = await adjustDb.OutboxMessages
            .Where(m => m.AggregateId == productId.Value.ToString())
            .ToListAsync();

        outbox.Should().Contain(m => m.Type == nameof(ProductStockUpdatedEvent),
            "AdjustStock raises ProductStockUpdatedEvent which must be written to the outbox");
    }

    [Fact]
    public async Task UpdateProductAsync_AfterAdjustStock_WhenDeltaExceedsStock_ShouldClampToZero()
    {
        await using var createScope = _fixture.CreateScope();
        var createSvc = createScope.ServiceProvider.GetRequiredService<IProductPersistenceService>();

        var product = BuildProduct("Clamp-Zero Product");
        await createSvc.CreateProductAsync(product);
        var productId = product.Id;

        await using var adjustScope = _fixture.CreateScope();
        var adjustSvc = adjustScope.ServiceProvider.GetRequiredService<IProductPersistenceService>();
        var adjustDb  = adjustScope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var loaded = await adjustSvc.GetByIdAsync(productId);
        loaded!.AdjustStock(-1000);
        await adjustSvc.UpdateProductAsync(loaded);

        var saved = await adjustDb.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId);

        saved!.StockQuantity.Should().Be(0, "delta larger than current stock must clamp to 0, not go negative");
    }
}
