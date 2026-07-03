using Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class FailedCompensationRetryConfiguration : IEntityTypeConfiguration<FailedCompensationRetry>
{
    public void Configure(EntityTypeBuilder<FailedCompensationRetry> builder)
    {
        builder.ToTable("FailedCompensationRetries");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SagaId).IsRequired();
        builder.Property(x => x.SagaType).IsRequired().HasMaxLength(128);
        builder.Property(x => x.LastFailedStep).IsRequired().HasMaxLength(256);
        builder.Property(x => x.RetryCount).IsRequired();
        builder.Property(x => x.NextAttemptAtUtc).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(2048);
        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
        builder.Property(x => x.CompletedAtUtc);

        builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });

        // 2 requests called concurrently to compensate the same saga, both call enqueue failed saga.
        // one will succeed because db will say no existing row, but for the second the row already exists
        // so the second insert will fail due to the unique constraint — only one retry row is ever created.
        builder.HasIndex(x => x.SagaId)
            .HasDatabaseName("IX_FailedCompensationRetries_SagaId_Active")
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 3)");
    }
}
