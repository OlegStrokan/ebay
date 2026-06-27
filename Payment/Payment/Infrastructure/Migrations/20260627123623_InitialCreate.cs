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
                name: "outbound_order_callbacks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    callback_event_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_order_callbacks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    event_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    processing_status = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processing_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_webhook_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    customer_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method = table.Column<int>(type: "integer", nullable: false),
                    process_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    provider_payment_intent_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    provider_refund_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    total_refunded_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    succeeded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_refund_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refunds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_order_callbacks_callback_event_id",
                table: "outbound_order_callbacks",
                column: "callback_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbound_order_callbacks_order_id",
                table: "outbound_order_callbacks",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_order_callbacks_status_next_retry_at",
                table: "outbound_order_callbacks",
                columns: new[] { "status", "next_retry_at" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_webhook_events_provider_event_id",
                table: "payment_webhook_events",
                column: "provider_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_order_id_process_idempotency_key",
                table: "payments",
                columns: new[] { "order_id", "process_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_status_updated_at",
                table: "payments",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_refunds_payment_id_idempotency_key",
                table: "refunds",
                columns: new[] { "payment_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refunds_payment_id_status",
                table: "refunds",
                columns: new[] { "payment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_refunds_status_updated_at",
                table: "refunds",
                columns: new[] { "status", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound_order_callbacks");

            migrationBuilder.DropTable(
                name: "payment_webhook_events");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "refunds");
        }
    }
}
