using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations.Write
{
    /// <inheritdoc />
    public partial class AddSagaParkCount : Migration
    {
        /// <inheritdoc />
        // Rows already parked when this ships start at 0, so they get the full park budget rather
        // than credit for the park they are currently sitting in. Over-generous by one, which is
        // the safe direction: it cannot fail a live saga on deploy.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParkCount",
                table: "SagaStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParkCount",
                table: "SagaStates");
        }
    }
}
