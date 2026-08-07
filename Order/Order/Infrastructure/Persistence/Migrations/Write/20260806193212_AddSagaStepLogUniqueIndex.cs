using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations.Write
{
    /// <inheritdoc />
    public partial class AddSagaStepLogUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Duplicates already exist in any environment that has resumed a saga, so the index
            // below cannot be created until they are gone.
            //
            // Keep a Compensated row above everything else: losing "this step was already rolled
            // back" is exactly what makes CompensationRetryWorker refund a second time, which is
            // the bug this migration exists to stop. Otherwise keep the newest row, because that
            // is what the upsert in SagaRepository.SaveStepAsync would have produced.
            migrationBuilder.Sql(
                """
                DELETE FROM "SagaStepLogs"
                WHERE "Id" IN (
                    SELECT "Id"
                    FROM (
                        SELECT "Id",
                               ROW_NUMBER() OVER (
                                   PARTITION BY "SagaId", "StepName"
                                   ORDER BY ("Status" = 'Compensated') DESC,
                                            "StartedAt" DESC,
                                            "Id" DESC
                               ) AS rn
                        FROM "SagaStepLogs"
                    ) ranked
                    WHERE rn > 1
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SagaStepLogs_SagaId_StepName",
                table: "SagaStepLogs",
                columns: new[] { "SagaId", "StepName" },
                unique: true);
        }

        /// <inheritdoc />
        // Rows deleted above are not recoverable. Down only lifts the constraint.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaStepLogs_SagaId_StepName",
                table: "SagaStepLogs");
        }
    }
}
