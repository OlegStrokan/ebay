using Application.Commands.ActivateProduct;
using Application.Commands.CreateProduct;
using Application.Commands.DeactivateProduct;
using Application.Commands.UpdateProduct;
using Application.Commands.UpdateProductStock;
using Application.DTOs;
using Domain.ValueObjects;
using Confluent.Kafka;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.ReadModels;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Product.E2ETests.Infrastructure;

public static class E2ETestServerExtensions
{
    public static async Task ResetAsync(this E2ETestServer server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Write-side tables first; Categories last for FK order.
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.Listings.ExecuteDeleteAsync();
        await db.CatalogItems.ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
        await db.Categories.ExecuteDeleteAsync();

        db.Categories.Add(new Category { Id = CategoryId.PlaceholderGuid, Name = "Uncategorized" });
        await db.SaveChangesAsync();
    }

    public static async Task<Guid> SeedCategoryAsync(
        this E2ETestServer server,
        string name = "Electronics")
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var id = Guid.NewGuid();
        db.Categories.Add(new Category { Id = id, Name = name });
        await db.SaveChangesAsync();
        return id;
    }
    
    public static async Task<Guid> CreateProductAsync(
        this E2ETestServer server,
        Guid sellerId,
        Guid categoryId,
        string name = "E2E Test Product",
        string description = "Created for E2E testing",
        decimal price = 49.99m,
        string currency = "USD",
        int stock = 10,
        List<ProductAttributeDto>? attributes = null,
        List<string>? imageUrls = null)
    {
        using var scope = server.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var cmd = new CreateProductCommand(
            sellerId,
            name,
            description,
            price,
            currency,
            stock,
            attributes ?? [],
            imageUrls ?? []);

        var result = await mediator.Send(cmd);

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"CreateProduct failed: {string.Join(", ", result.Errors)}");

        var assignCategory = await mediator.Send(new UpdateProductCommand(
            ProductId: result.Value!,
            Name: name,
            Description: description,
            CategoryId: categoryId,
            Price: price,
            Currency: currency,
            Attributes: attributes ?? [],
            ImageUrls: imageUrls ?? []));

        if (!assignCategory.IsSuccess)
            throw new InvalidOperationException(
                $"Assign category failed: {string.Join(", ", assignCategory.Errors)}");

        return result.Value!;
    }

    public static async Task ActivateProductAsync(this E2ETestServer server, Guid productId)
    {
        using var scope = server.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ActivateProductCommand(productId));

        if (!result.IsSuccess)
        {
            if (result.Errors.Any(e => e.Contains("Cannot transition from Approved to Approved")
                                       || e.Contains("Cannot transition from Active to Active")))
                return;

            throw new InvalidOperationException(
                $"ActivateProduct failed: {string.Join(", ", result.Errors)}");
        }
    }

    public static async Task DeactivateProductAsync(this E2ETestServer server, Guid productId)
    {
        await server.ActivateProductAsync(productId);

        using var scope = server.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new DeactivateProductCommand(productId));

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"DeactivateProduct failed: {string.Join(", ", result.Errors)}");
    }

    public static async Task UpdateStockAsync(this E2ETestServer server, Guid productId, int newQuantity)
    {
        using var scope = server.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new UpdateProductStockCommand(productId, newQuantity));

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"UpdateStock failed: {string.Join(", ", result.Errors)}");
    }

    public static Task<bool> WaitForKafkaEventAsync(
        this E2ETestServer server,
        Guid aggregateId,
        string eventType,
        int timeoutSeconds = 20)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = server.KafkaBootstrapServers,
            GroupId          = $"e2e-verify-{Guid.NewGuid()}",
            AutoOffsetReset  = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("product.events");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var msg = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (msg?.Message?.Value is null) continue;

                    if (msg.Message.Value.Contains(aggregateId.ToString()) &&
                        msg.Message.Value.Contains(eventType))
                        return Task.FromResult(true);
                }
                catch (Confluent.Kafka.ConsumeException)
                {
                    // Topic may not exist yet - keep polling until timeout
                }
            }
        }
        finally
        {
            consumer.Unsubscribe();
        }

        return Task.FromResult(false);
    }
}
