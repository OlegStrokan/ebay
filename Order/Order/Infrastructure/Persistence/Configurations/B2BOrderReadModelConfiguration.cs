using Infrastructure.ReadModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class B2BOrderReadModelConfiguration : IEntityTypeConfiguration<B2BOrderReadModel>
{
    public void Configure(EntityTypeBuilder<B2BOrderReadModel> builder)
    {
        builder.ToTable("B2BOrderReadModels");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).IsRequired().ValueGeneratedNever();

        builder.Property(b => b.CustomerId).IsRequired();
        builder.Property(b => b.CompanyId);
        builder.Property(b => b.CompanyName).IsRequired().HasMaxLength(200);
        builder.Property(b => b.Status).IsRequired().HasMaxLength(50);

        builder.Property(b => b.TotalPrice).IsRequired().HasPrecision(18, 2);
        builder.Property(b => b.Currency).IsRequired().HasMaxLength(3);
        builder.Property(b => b.DiscountPercent).IsRequired().HasPrecision(5, 2);

        builder.Property(b => b.RequestedDeliveryDate);
        builder.Property(b => b.FinalizedOrderId);

        builder.Property(b => b.DeliveryStreet).IsRequired().HasMaxLength(200);
        builder.Property(b => b.DeliveryCity).IsRequired().HasMaxLength(100);
        builder.Property(b => b.DeliveryCountry).IsRequired().HasMaxLength(100);
        builder.Property(b => b.DeliveryPostalCode).IsRequired().HasMaxLength(20);

        builder.Property(b => b.ItemsJson).IsRequired().HasColumnType("text");
        builder.Property(b => b.CommentsJson).IsRequired().HasColumnType("text");

        builder.Property(b => b.StartedAt).IsRequired();
        builder.Property(b => b.UpdatedAt);
        builder.Property(b => b.Version).IsRequired();
        builder.Property(b => b.LastSyncedAt).IsRequired();

        // common query indexes
        builder.HasIndex(b => b.CustomerId)
            .HasDatabaseName("IX_B2BOrderReadModels_CustomerId");
        builder.HasIndex(b => b.CompanyName)
            .HasDatabaseName("IX_B2BOrderReadModels_CompanyName");
        builder.HasIndex(b => b.Status)
            .HasDatabaseName("IX_B2BOrderReadModels_Status");
        builder.HasIndex(b => b.StartedAt)
            .HasDatabaseName("IX_B2BOrderReadModels_StartedAt");
        builder.HasIndex(b => new { b.StartedAt, b.Id })
            .HasDatabaseName("IX_B2BOrderReadModels_StartedAt_Id");
    }
}
