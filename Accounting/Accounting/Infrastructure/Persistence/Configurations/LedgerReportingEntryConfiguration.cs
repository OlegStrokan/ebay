using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

internal sealed class LedgerReportingEntryConfiguration : IEntityTypeConfiguration<LedgerReportingEntry>
{
    public void Configure(EntityTypeBuilder<LedgerReportingEntry> builder)
    {
        builder.ToTable("ledger_reporting_entries");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.TransactionId)
            .HasColumnName("transaction_id")
            .IsRequired();

        builder.Property(x => x.EntryId).HasColumnName("entry_id");

        builder.Property(x => x.Account)
            .HasColumnName("account")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.Direction)
            .HasColumnName("direction")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(x => x.ReportingAmount)
            .HasColumnName("reporting_amount")
            .HasPrecision(19, 4)
            .IsRequired();

        builder.Property(x => x.ReportingCurrency)
            .HasColumnName("reporting_currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.RateUsed)
            .HasColumnName("rate_used")
            .HasPrecision(19, 8)
            .IsRequired();

        builder.Property(x => x.ConvertedAt)
            .HasColumnName("converted_at")
            .IsRequired();

        builder.HasIndex(x => x.TransactionId);

        // One reporting row per primitive entry, so a rebuild cannot silently double-count.
        // The synthetic fx_gain_loss leg has a null entry_id and is excluded by the filter.
        builder.HasIndex(x => x.EntryId)
            .IsUnique()
            .HasFilter("entry_id IS NOT NULL");
    }
}
