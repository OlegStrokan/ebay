using Application.Sagas.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class SagaStateConfiguration : IEntityTypeConfiguration<SagaState>
{
    public void Configure(EntityTypeBuilder<SagaState> builder)
    {
        builder.ToTable("SagaStates");

        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.CorrelationId, x.SagaType}).IsUnique();

        builder.Property(x => x.SagaType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.CurrentStep).IsRequired().HasMaxLength(100);

        builder.Property(x => x.Context).HasColumnType("text");
        builder.Property(x => x.Payload).HasColumnType("text");

        builder.Property(x => x.WaitReason).HasMaxLength(512);

        // computed from ParkCount - no column, and EF cannot map a getter with no backing field
        builder.Ignore(x => x.IsWithinParkBudget);

        builder.HasIndex(x => x.UpdatedAt);

        builder.HasIndex(x => new { x.Status, x.UpdatedAt });

        builder.HasIndex(x => x.WaitDeadlineUtc)
            .HasFilter("\"WaitDeadlineUtc\" IS NOT NULL");

        builder.HasMany(x => x.Steps)
            .WithOne()
            .HasForeignKey(x => x.SagaId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}