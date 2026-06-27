using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_movements",
                columns: table => new
                {
                    MovementId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovementType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QuantityDelta = table.Column<int>(type: "integer", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movements", x => x.MovementId);
                });

            migrationBuilder.CreateTable(
                name: "inventory_outbox_messages",
                columns: table => new
                {
                    OutboxMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_outbox_messages", x => x.OutboxMessageId);
                });

            migrationBuilder.CreateTable(
                name: "inventory_reservations",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_reservations", x => x.ReservationId);
                });

            migrationBuilder.CreateTable(
                name: "product_stocks",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvailableQuantity = table.Column<int>(type: "integer", nullable: false),
                    ReservedQuantity = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_stocks", x => x.ProductId);
                    table.CheckConstraint("ck_product_stocks_available_non_negative", "\"AvailableQuantity\" >= 0");
                    table.CheckConstraint("ck_product_stocks_reserved_non_negative", "\"ReservedQuantity\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "inventory_reservation_items",
                columns: table => new
                {
                    ReservationItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_reservation_items", x => x.ReservationItemId);
                    table.ForeignKey(
                        name: "FK_inventory_reservation_items_inventory_reservations_Reservat~",
                        column: x => x.ReservationId,
                        principalTable: "inventory_reservations",
                        principalColumn: "ReservationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_CreatedAtUtc",
                table: "inventory_movements",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ProductId",
                table: "inventory_movements",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_outbox_messages_CreatedAtUtc",
                table: "inventory_outbox_messages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_outbox_messages_ProcessedAtUtc",
                table: "inventory_outbox_messages",
                column: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_items_ProductId",
                table: "inventory_reservation_items",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservation_items_ReservationId",
                table: "inventory_reservation_items",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reservations_OrderId",
                table: "inventory_reservations",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_movements");

            migrationBuilder.DropTable(
                name: "inventory_outbox_messages");

            migrationBuilder.DropTable(
                name: "inventory_reservation_items");

            migrationBuilder.DropTable(
                name: "product_stocks");

            migrationBuilder.DropTable(
                name: "inventory_reservations");
        }
    }
}
