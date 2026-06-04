using Application.Interfaces;
using Domain.Entities;
using Domain.Interfaces;
using Domain.ValueObjects;
using FluentAssertions;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Product.IntegrationTests.Infrastructure;
using Xunit;
using ProductEntity = Domain.Entities.Product;

namespace Product.IntegrationTests.Persistence;

[Collection("Integration")]
public sealed class ProductRepositoryTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public ProductRepositoryTests(IntegrationFixture fixture) => _fixture = fixture;

    private static ProductEntity BuildProduct() =>
        ProductEntity.Create(
            SellerId.CreateUnique(),
            "Test Widget",
            "A test product description",
            CategoryId.CreateUnique(),
            Money.Create(29.99m, "USD"),
            10,
            [],
            []);

    [Fact]
    public async Task AddAsync_ShouldPersistProduct_AndGetByIdShouldReturnIt()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var db   = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product = BuildProduct();
        var productId = product.Id;

        await repo.AddAsync(product);
        await db.SaveChangesAsync();

        // detach so the next read hits the DB
        db.ChangeTracker.Clear();

        var loaded = await repo.GetByIdAsync(productId);

        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be(productId.Value);
        loaded.Name.Should().Be("Test Widget");
        loaded.Price.Amount.Should().Be(29.99m);
        loaded.Price.Currency.Should().Be("USD");
        loaded.StockQuantity.Should().Be(10);
        loaded.Status.Should().Be(ProductStatus.Draft);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenProductDoesNotExist()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductRepository>();

        var result = await repo.GetByIdAsync(ProductId.CreateUnique());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ShouldPersistMutations()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var db   = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product = BuildProduct();
        await repo.AddAsync(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // reload -> mutate -> save
        var loaded = await repo.GetByIdAsync(product.Id);
        loaded!.Activate();
        loaded.ClearDomainEvents();
        await repo.UpdateAsync(loaded);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var updated = await repo.GetByIdAsync(product.Id);

        updated!.Status.Should().Be(ProductStatus.Approved,
            "Activate() must be reflected after UpdateAsync + SaveChanges");
    }

    [Fact]
    public async Task AddAsync_ShouldPersistAttributes_AsJsonb()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var db   = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var attributes = new List<ProductAttribute>
        {
            new("Color", "Red"),
            new("Size", "Large")
        };
        var product = ProductEntity.Create(
            SellerId.CreateUnique(),
            "Attributed Product",
            "desc",
            CategoryId.CreateUnique(),
            Money.Create(10m, "USD"),
            0,
            attributes,
            ["https://example.com/img.png"]);

        await repo.AddAsync(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await repo.GetByIdAsync(product.Id);

        loaded!.Attributes.Should().HaveCount(2);
        loaded.Attributes.Should().Contain(a => a.Key == "color" && a.Value == "Red");
        loaded.ImageUrls.Should().ContainSingle("https://example.com/img.png");
    }
}
