using Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class AdminActionAuditEntryConfiguration : IEntityTypeConfiguration<AdminActionAuditEntry>
{
    public void Configure(EntityTypeBuilder<AdminActionAuditEntry> builder)
    {
        builder.ToTable("AdminActionAuditEntries");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action).IsRequired().HasMaxLength(64);
        builder.Property(x => x.TargetId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.OperatorSubject).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Success).IsRequired();
        builder.Property(x => x.Detail).HasMaxLength(2048);
        builder.Property(x => x.OccurredAtUtc).IsRequired();

        builder.HasIndex(x => x.TargetId);
    }
}
