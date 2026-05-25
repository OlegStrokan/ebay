using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetTokenEntity>
{
    public void Configure(EntityTypeBuilder<PasswordResetTokenEntity> builder)
    {
        builder.ToTable("PasswordResetTokens");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).IsRequired().HasMaxLength(26);
        builder.Property(x => x.UserId).IsRequired().HasMaxLength(36);
        builder.Property(x => x.Token).IsRequired().HasMaxLength(100);
        builder.Property(x => x.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(x => x.IsUsed).IsRequired();
        builder.Property(x => x.UsedAt);
        builder.Property(x => x.IpAddress).HasMaxLength(45); // ipv6 max length
        
        
        // indexes
        builder.HasIndex(x => x.Token).IsUnique();
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.ExpiresAt);

    }
}