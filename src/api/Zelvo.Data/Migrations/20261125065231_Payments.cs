using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Payments : Migration
    {
        private static readonly string[] Tables = ["payment_gateways", "payments", "payment_attempts", "payment_refunds", "payment_reconciliations"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_gateways",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    merchant_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    connector = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    encrypted_credentials = table.Column<string>(type: "text", nullable: true),
                    credential_names = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    test_payment_succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_gateways", x => x.id);
                    table.CheckConstraint("ck_payment_gateways_mode", "mode IN ('Test', 'Live')");
                    table.ForeignKey(
                        name: "fk_payment_gateways_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    mismatched = table.Column<int>(type: "integer", nullable: false),
                    unknown_to_zelvo = table.Column<int>(type: "integer", nullable: false),
                    total_aed = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    file_imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_reconciliations", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_reconciliations_payment_gateways_gateway_id",
                        column: x => x.gateway_id,
                        principalTable: "payment_gateways",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_payment_reconciliations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gateway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_aed = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    breakdown = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    timing = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    paid_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    receipt_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    receipt_sequence = table.Column<long>(type: "bigint", nullable: true),
                    refunded_aed = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount_aed > 0 AND refunded_aed >= 0 AND refunded_aed <= amount_aed");
                    table.CheckConstraint("ck_payments_state", "state IN ('AwaitingPayment', 'Paid', 'Expired', 'Cancelled', 'PartlyRefunded', 'Refunded')");
                    table.CheckConstraint("ck_payments_ticket", "is_test OR ticket_id IS NOT NULL");
                    table.CheckConstraint("ck_payments_timing", "timing IN ('OnSubmission', 'AfterApproval', 'OnCompletion')");
                    table.ForeignKey(
                        name: "fk_payments_payment_gateways_gateway_id",
                        column: x => x.gateway_id,
                        principalTable: "payment_gateways",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    gateway_status = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    started_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_attempts", x => x.id);
                    table.CheckConstraint("ck_payment_attempts_state", "state IN ('Redirected', 'Paid', 'Failed', 'Cancelled', 'Error')");
                    table.ForeignKey(
                        name: "fk_payment_attempts_payment_gateways_gateway_id",
                        column: x => x.gateway_id,
                        principalTable: "payment_gateways",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_attempts_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_payment_attempts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_attempts_users_started_by_id",
                        column: x => x.started_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "payment_refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_aed = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    gateway_refund_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_refunds", x => x.id);
                    table.CheckConstraint("ck_payment_refunds_amount", "amount_aed > 0");
                    table.CheckConstraint("ck_payment_refunds_state", "state IN ('Completed', 'Failed', 'Manual')");
                    table.ForeignKey(
                        name: "fk_payment_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_payment_refunds_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_gateway_id",
                table: "payment_attempts",
                column: "gateway_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_gateway_id_transaction_id",
                table: "payment_attempts",
                columns: new[] { "gateway_id", "transaction_id" },
                unique: true,
                filter: "transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_payment_id",
                table: "payment_attempts",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_reference",
                table: "payment_attempts",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_started_by_id",
                table: "payment_attempts",
                column: "started_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_tenant_id",
                table: "payment_attempts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_gateways_default",
                table: "payment_gateways",
                column: "tenant_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_payment_gateways_tenant_id_name",
                table: "payment_gateways",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_reconciliations_gateway_id",
                table: "payment_reconciliations",
                column: "gateway_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_reconciliations_tenant_id_gateway_id_day",
                table: "payment_reconciliations",
                columns: new[] { "tenant_id", "gateway_id", "day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_created_by_id",
                table: "payment_refunds",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_payment_id",
                table: "payment_refunds",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id",
                table: "payment_refunds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_gateway_id",
                table: "payments",
                column: "gateway_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_paid_at",
                table: "payments",
                columns: new[] { "tenant_id", "paid_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_receipt_sequence",
                table: "payments",
                columns: new[] { "tenant_id", "receipt_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_state_expires_at",
                table: "payments",
                columns: new[] { "tenant_id", "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_ticket_id",
                table: "payments",
                column: "ticket_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The payments job finds tenants with gateways.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_payment_job_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active' AND EXISTS (SELECT 1 FROM public.payment_gateways g WHERE g.tenant_id = t.id)
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_payment_job_tenants();");
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "payment_attempts");

            migrationBuilder.DropTable(
                name: "payment_reconciliations");

            migrationBuilder.DropTable(
                name: "payment_refunds");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "payment_gateways");
        }
    }
}
