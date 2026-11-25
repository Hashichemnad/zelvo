using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports");

            migrationBuilder.AlterColumn<Guid>(
                name: "entity_id",
                table: "document_exports",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "parameters",
                table: "document_exports",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "report_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    report = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: true),
                    day_of_month = table.Column<int>(type: "integer", nullable: true),
                    time_of_day = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_schedules", x => x.id);
                    table.CheckConstraint("ck_report_schedules_day_of_month", "(frequency = 'Monthly') = (day_of_month IS NOT NULL) AND (day_of_month IS NULL OR day_of_month BETWEEN 1 AND 28)");
                    table.CheckConstraint("ck_report_schedules_day_of_week", "(frequency = 'Weekly') = (day_of_week IS NOT NULL) AND (day_of_week IS NULL OR day_of_week BETWEEN 1 AND 7)");
                    table.CheckConstraint("ck_report_schedules_frequency", "frequency IN ('Daily', 'Weekly', 'Monthly')");
                    table.CheckConstraint("ck_report_schedules_report", "report IN ('Overview', 'Volumes', 'Backlog', 'Sla', 'Agents', 'Happiness')");
                    table.CheckConstraint("ck_report_schedules_time_of_day", "time_of_day BETWEEN 0 AND 1439");
                    table.ForeignKey(
                        name: "fk_report_schedules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_schedules_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_deliveries", x => x.id);
                    table.CheckConstraint("ck_report_deliveries_status", "status IN ('Pending', 'Sent', 'Failed', 'Skipped')");
                    table.ForeignKey(
                        name: "fk_report_deliveries_report_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "report_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_report_deliveries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_deliveries_users_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_schedule_recipients",
                columns: table => new
                {
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_schedule_recipients", x => new { x.schedule_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_report_schedule_recipients_report_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "report_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_report_schedule_recipients_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_schedule_recipients_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports",
                sql: "kind IN ('Incident', 'ServiceRequest', 'Report')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_subject",
                table: "document_exports",
                sql: "(kind = 'Report') = (entity_id IS NULL AND parameters IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_report_deliveries_due",
                table: "report_deliveries",
                column: "next_attempt_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_report_deliveries_recipient_id",
                table: "report_deliveries",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_deliveries_schedule_id_recipient_id_run_at",
                table: "report_deliveries",
                columns: new[] { "schedule_id", "recipient_id", "run_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_report_deliveries_tenant_id_created_at",
                table: "report_deliveries",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_report_schedule_recipients_tenant_id",
                table: "report_schedule_recipients",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_schedule_recipients_user_id",
                table: "report_schedule_recipients",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_due",
                table: "report_schedules",
                column: "next_run_at",
                filter: "active AND next_run_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_owner_id",
                table: "report_schedules",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_tenant_id",
                table: "report_schedules",
                column: "tenant_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Tenants with scheduled reports due or emails of them waiting for the worker.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_report_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND (EXISTS (SELECT 1 FROM public.report_schedules s
                                   WHERE s.tenant_id = t.id AND s.active AND s.next_run_at <= now() AND (s.lease_until IS NULL OR s.lease_until < now()))
                           OR EXISTS (SELECT 1 FROM public.report_deliveries d
                                      WHERE d.tenant_id = t.id AND d.status = 'Pending' AND d.next_attempt_at <= now() AND (d.lease_until IS NULL OR d.lease_until < now())))
                $$;
                """);

            // The delivery log is kept 30 days.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_report_deliveries(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_count bigint;
                BEGIN
                    DELETE FROM public.report_deliveries WHERE created_at < p_before AND status <> 'Pending';
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END;
                $$;
                """);
        }

        private static readonly string[] Tables = ["report_schedules", "report_schedule_recipients", "report_deliveries"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_report_deliveries(timestamptz);
                DROP FUNCTION zelvo_report_tenants();
                DELETE FROM document_exports WHERE kind = 'Report';
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "report_deliveries");

            migrationBuilder.DropTable(
                name: "report_schedule_recipients");

            migrationBuilder.DropTable(
                name: "report_schedules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_subject",
                table: "document_exports");

            migrationBuilder.DropColumn(
                name: "parameters",
                table: "document_exports");

            migrationBuilder.AlterColumn<Guid>(
                name: "entity_id",
                table: "document_exports",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports",
                sql: "kind IN ('Incident', 'ServiceRequest')");
        }
    }
}
