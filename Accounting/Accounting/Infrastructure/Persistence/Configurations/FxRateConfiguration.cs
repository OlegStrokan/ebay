using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

internal sealed class FxRateConfiguration : IEntityTypeConfiguration<FxRate>
{
    public void Configure(EntityTypeBuilder<FxRate> builder)
    {
        builder.ToTable("fx_rates");

        builder.HasKey(x => new { x.BaseCurrency, x.QuoteCurrency, x.EffectiveFrom });

        builder.Property(x => x.BaseCurrency)
            .HasColumnName("base_currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.QuoteCurrency)
            .HasColumnName("quote_currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.Rate)
            .HasColumnName("rate")
            .HasPrecision(19, 8)
            .IsRequired();

        builder.Property(x => x.EffectiveFrom)
            .HasColumnName("effective_from")
            .IsRequired();
    }
}
