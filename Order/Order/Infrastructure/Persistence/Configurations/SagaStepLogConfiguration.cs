using Application.Sagas.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class SagaStepLogConfiguration : IEntityTypeConfiguration<SagaStepLog>
{
    public void Configure(EntityTypeBuilder<SagaStepLog> builder)
    {
        builder.ToTable("SagaStepLogs");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        builder.HasIndex(x => x.SagaId);

        builder.HasIndex(x => new { x.SagaId, x.StepName }).IsUnique();
    }
}