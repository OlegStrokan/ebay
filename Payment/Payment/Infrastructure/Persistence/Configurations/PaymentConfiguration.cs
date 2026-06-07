using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");

        builder.HasKey(x => x.Id);

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasMaxLength(64)
            .HasConversion(ValueObjectConverters.PaymentId);

        builder.Property(x => x.OrderId)
            .HasColumnName("order_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.CustomerId)
            .HasColumnName("customer_id")
            .HasMaxLength(128)
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

        builder.Property(x => x.Method)
            .HasColumnName("method")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.ProcessIdempotencyKey)
            .HasColumnName("process_idempotency_key")
            .HasMaxLength(128)
            .HasConversion(ValueObjectConverters.IdempotencyKey)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.ProviderPaymentIntentId)
            .HasColumnName("provider_payment_intent_id")
            .HasMaxLength(128)
            .HasConversion(ValueObjectConverters.NullableProviderPaymentIntentId);

        builder.Property(x => x.ProviderRefundId)
            .HasColumnName("provider_refund_id")
            .HasMaxLength(128)
            .HasConversion(ValueObjectConverters.NullableProviderRefundId);

        builder.Property(x => x.TotalRefundedAmount)
            .HasColumnName("total_refunded_amount")
            .HasPrecision(18, 2)
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

        builder.Property(x => x.SucceededAt)
            .HasColumnName("succeeded_at");

        builder.Property(x => x.FailedAt)
            .HasColumnName("failed_at");

        builder.HasIndex(x => new { x.OrderId, x.ProcessIdempotencyKey }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.UpdatedAt });

        builder.Ignore(x => x.DomainEvents);
    }
}