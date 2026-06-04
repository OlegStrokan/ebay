using Infrastructure.Persistence.ReadModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.ValueObjects;

namespace Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(x => x.ParentId);

        builder.HasIndex(x => x.ParentId);

        builder.HasData(new Category
        {
            Id = CategoryId.PlaceholderGuid,
            Name = "Uncategorized",
            ParentId = null
        });
    }
}
