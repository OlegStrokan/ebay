using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds");

        builder.HasKey(x => x.Id);

        // prevents two concurrent writers (webhook + reconciliation)
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasMaxLength(64)
            .HasConversion(ValueObjectConverters.RefundId);

        builder.Property(x => x.PaymentId)
            .HasColumnName("payment_id")
            .HasMaxLength(64)
            .HasConversion(ValueObjectConverters.PaymentId)
            .IsRequired();

        builder.OwnsOne(x => x.Amount, amount =>
        {
            amount.Property(x => x.Amount)
                .HasColumnName("amount")
                .HasPrecision(18, 2)
                .IsRequired();

            amount.Property(x => x.Currency)
                .HasColumnName("currency")
                .HasMaxLength(3)
                .IsRequired();
        });
        builder.Navigation(x => x.Amount).IsRequired();

        builder.Property(x => x.Reason)
            .HasColumnName("reason")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .HasConversion(ValueObjectConverters.IdempotencyKey)
            .IsRequired();

        builder.Property(x => x.ProviderRefundId)
            .HasColumnName("provider_refund_id")
            .HasMaxLength(128)
            .HasConversion(ValueObjectConverters.NullableProviderRefundId);

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired();

        builder.OwnsOne(x => x.FailureReason, failure =>
        {
            failure.Property(x => x.Code)
                .HasColumnName("failure_code")
                .HasMaxLength(64);

            failure.Property(x => x.Message)
                .HasColumnName("failure_message")
                .HasMaxLength(1024);
        });
        builder.Navigation(x => x.FailureReason).IsRequired(false);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at");

        builder.HasIndex(x => new { x.PaymentId, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => new { x.PaymentId, x.Status });
        builder.HasIndex(x => new { x.Status, x.UpdatedAt });
    }
}