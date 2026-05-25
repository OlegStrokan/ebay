using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshTokenEntity>
{
    public void Configure(EntityTypeBuilder<RefreshTokenEntity> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(rt => rt.Id);
        
        builder.Property(x => x.Id).IsRequired().HasMaxLength(26);
        builder.Property(x => x.UserId).IsRequired().HasMaxLength(36);
        builder.Property(x => x.Token).IsRequired().HasMaxLength(500);
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(x => x.IsRevoked).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.RevokedAt);
        builder.Property(x => x.RevokedById).HasMaxLength(26);
        builder.Property(x => x.ReplacedByToken).HasMaxLength(500);
        
        // Indexes for performance
        builder.HasIndex(x => x.Token).IsUnique();
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.ExpiresAt);
    }

   
}