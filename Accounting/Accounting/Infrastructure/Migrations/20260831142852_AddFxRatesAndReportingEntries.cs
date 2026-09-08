using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFxRatesAndReportingEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fx_rates",
                columns: table => new
                {
                    base_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    quote_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(19,8)", precision: 19, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fx_rates", x => new { x.base_currency, x.quote_currency, x.effective_from });
                });

            migrationBuilder.CreateTable(
                name: "ledger_reporting_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account = table.Column<int>(type: "integer", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    reporting_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reporting_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate_used = table.Column<decimal>(type: "numeric(19,8)", precision: 19, scale: 8, nullable: false),
                    converted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_reporting_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_reporting_entries_entry_id",
                table: "ledger_reporting_entries",
                column: "entry_id",
                unique: true,
                filter: "entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_reporting_entries_transaction_id",
                table: "ledger_reporting_entries",
                column: "transaction_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fx_rates");

            migrationBuilder.DropTable(
                name: "ledger_reporting_entries");
        }
    }
}
