using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

internal sealed class OutboundMoneyEventConfiguration : IEntityTypeConfiguration<OutboundMoneyEvent>
{
    public void Configure(EntityTypeBuilder<OutboundMoneyEvent> builder)
    {
        builder.ToTable("outbound_money_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EventId).HasColumnName("event_id").HasMaxLength(64).IsRequired();
        builder.Property(x => x.PaymentId).HasColumnName("payment_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.OrderId).HasColumnName("order_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(128).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json")
            .HasColumnType("text").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at");
        builder.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2048);

        builder.HasIndex(x => x.EventId).IsUnique();
        builder.HasIndex(x => new { x.Status, x.NextRetryAt });
        builder.HasIndex(x => x.PaymentId);
    }
}
