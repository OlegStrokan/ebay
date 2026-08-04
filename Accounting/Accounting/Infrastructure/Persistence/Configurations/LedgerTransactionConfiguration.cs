using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

internal sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.ToTable("ledger_transactions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.TransactionRef)
            .HasColumnName("transaction_ref")
            .HasMaxLength(256)
            .IsRequired();

        builder.HasIndex(x => x.TransactionRef)
            .IsUnique();

        builder.Property(x => x.OrderId)
            .HasColumnName("order_id");

        builder.Property(x => x.PaymentId)
            .HasColumnName("payment_id");

        builder.Property(x => x.RefType)
            .HasColumnName("ref_type")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.RefId)
            .HasColumnName("ref_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasMany(x => x.Entries)
            .WithOne()
            .HasForeignKey(e => e.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Entries)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_entries");
    }
}
