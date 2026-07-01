using Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class PplPendingBookingConfiguration : IEntityTypeConfiguration<PplPendingBooking>
{
    public void Configure(EntityTypeBuilder<PplPendingBooking> builder)
    {
        builder.ToTable("PplPendingBookings");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.ReferenceId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.NextRetryAtUtc).IsRequired();
        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.Property(x => x.LastError).HasMaxLength(2048);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        builder.HasIndex(x => new { x.Status, x.NextRetryAtUtc });

        // One active (Pending=0 or InProgress=4) row per order at a time.
        builder.HasIndex(x => x.OrderId)
            .HasDatabaseName("IX_PplPendingBookings_OrderId_Active")
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 4)");
    }
}
