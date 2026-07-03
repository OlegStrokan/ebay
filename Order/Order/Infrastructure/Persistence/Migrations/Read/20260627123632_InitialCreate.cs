using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations.Read
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "B2BOrderReadModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    RequestedDeliveryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinalizedOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryStreet = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeliveryCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryCountry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    CommentsJson = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_B2BOrderReadModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderReadModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaymentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DeliveryStreet = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeliveryCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryCountry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderReadModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringOrderReadModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalExecutions = table.Column<int>(type: "integer", nullable: false),
                    MaxExecutions = table.Column<int>(type: "integer", nullable: true),
                    DeliveryStreet = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeliveryCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryCountry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringOrderReadModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReturnRequestReadModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ItemsToReturnJson = table.Column<string>(type: "text", maxLength: 3, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnRequestReadModels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_B2BOrderReadModels_CompanyName",
                table: "B2BOrderReadModels",
                column: "CompanyName");

            migrationBuilder.CreateIndex(
                name: "IX_B2BOrderReadModels_CustomerId",
                table: "B2BOrderReadModels",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_B2BOrderReadModels_StartedAt",
                table: "B2BOrderReadModels",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_B2BOrderReadModels_StartedAt_Id",
                table: "B2BOrderReadModels",
                columns: new[] { "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_B2BOrderReadModels_Status",
                table: "B2BOrderReadModels",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "OrderReadModels_CreatedAt",
                table: "OrderReadModels",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "OrderReadModels_CreatedAt_Id",
                table: "OrderReadModels",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "OrderReadModels_CustomerId",
                table: "OrderReadModels",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "OrderReadModels_Status",
                table: "OrderReadModels",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "OrderReadModels_TrackingId",
                table: "OrderReadModels",
                column: "TrackingId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrders_CustomerId",
                table: "RecurringOrderReadModels",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrders_Status_NextRunAt",
                table: "RecurringOrderReadModels",
                columns: new[] { "Status", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequestReadModel_CustomerId",
                table: "ReturnRequestReadModels",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequestReadModels_OrderId",
                table: "ReturnRequestReadModels",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequestReadModels_RequestedAt",
                table: "ReturnRequestReadModels",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequestReadModels_RequestedAt_Id",
                table: "ReturnRequestReadModels",
                columns: new[] { "RequestedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequestReadModels_Status",
                table: "ReturnRequestReadModels",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "B2BOrderReadModels");

            migrationBuilder.DropTable(
                name: "OrderReadModels");

            migrationBuilder.DropTable(
                name: "RecurringOrderReadModels");

            migrationBuilder.DropTable(
                name: "ReturnRequestReadModels");
        }
    }
}
