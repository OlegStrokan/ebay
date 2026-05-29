using System.Text.Json;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasConversion(id => id.Value, value => ProductId.From(value))
            .ValueGeneratedNever();

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(x => x.Description)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(x => x.StockQuantity)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt);

        builder.Property(x => x.ReviewNotes)
            .HasMaxLength(500);

        builder.Property(x => x.SellerId)
            .HasConversion(id => id.Value, value => SellerId.From(value))
            .HasColumnName("SellerId")
            .IsRequired();

        builder.Property(x => x.CategoryId)
            .HasConversion(id => id.Value, value => CategoryId.From(value))
            .HasColumnName("CategoryId")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion(s => s.Value, v => ProductStatus.FromValue(v))
            .HasColumnName("Status")
            .IsRequired();

        builder.ComplexProperty(x => x.Price, priceBuilder =>
        {
            priceBuilder.Property(m => m.Amount)
                .HasColumnName("Price")
                .HasColumnType("numeric(18,2)")
                .IsRequired();

            priceBuilder.Property(m => m.Currency)
                .HasColumnName("Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        var attributesComparer = new ValueComparer<List<ProductAttribute>>(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                   == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            c => c.GetHashCode(),
            c => c.ToList());

        builder.Property<List<ProductAttribute>>("_attributes")
            .HasColumnName("Attributes")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<ProductAttribute>>(v, (JsonSerializerOptions?)null) ?? new List<ProductAttribute>(),
                attributesComparer)
            .IsRequired();

        var imageUrlsComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            c => c.GetHashCode(),
            c => c.ToList());

        builder.Property<List<string>>("_imageUrls")
            .HasColumnName("ImageUrls")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                imageUrlsComparer)
            .IsRequired();

        builder.HasIndex(x => x.SellerId);
        builder.HasIndex(x => x.CategoryId);
        builder.HasIndex(x => x.Status);

        // to prevent "productattribute" requires a primary key to be defined issue:
        // "_attributes" and "_imageUrls" are mapped above as jsonb columns via their private
        // backing fields; ignore the public read-only wrappers so EF Core does not try to
        // discover ProductAttribute as an entity type
        builder.Ignore(x => x.Attributes);
        builder.Ignore(x => x.ImageUrls);
    }
}
