using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations.Write
{
    /// <inheritdoc />
    public partial class AddSagaWaitDeadline : Migration
    {
        /// <inheritdoc />
        // Sagas already parked when this ships keep WaitDeadlineUtc = NULL and are therefore
        // still invisible to the watchdog. That is deliberate: backfilling a deadline would
        // mass-compensate every in-flight park on deploy. Sweep them once from the Ops Console
        // (SagaStates WHERE Status = WaitingForEvent AND WaitDeadlineUtc IS NULL).
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WaitDeadlineUtc",
                table: "SagaStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitReason",
                table: "SagaStates",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitRecoveryMode",
                table: "SagaStates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WaitingSinceUtc",
                table: "SagaStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SagaStates_WaitDeadlineUtc",
                table: "SagaStates",
                column: "WaitDeadlineUtc",
                filter: "\"WaitDeadlineUtc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaStates_WaitDeadlineUtc",
                table: "SagaStates");

            migrationBuilder.DropColumn(
                name: "WaitDeadlineUtc",
                table: "SagaStates");

            migrationBuilder.DropColumn(
                name: "WaitReason",
                table: "SagaStates");

            migrationBuilder.DropColumn(
                name: "WaitRecoveryMode",
                table: "SagaStates");

            migrationBuilder.DropColumn(
                name: "WaitingSinceUtc",
                table: "SagaStates");
        }
    }
}
