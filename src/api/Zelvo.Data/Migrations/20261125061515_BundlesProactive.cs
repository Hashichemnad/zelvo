using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class BundlesProactive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings");

            migrationBuilder.AddColumn<bool>(
                name: "held",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "bundle_id",
                table: "service_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bundle_optional",
                table: "service_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "bundle_stage",
                table: "service_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hold",
                table: "service_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<bool>(
                name: "is_bundle",
                table: "service_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "proactive_run_id",
                table: "service_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "proactive_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    answers = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    api_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proactive_runs", x => x.id);
                    table.CheckConstraint("ck_proactive_runs_kind", "kind IN ('Date', 'Schedule', 'Event')");
                    table.CheckConstraint("ck_proactive_runs_state", "state IN ('Scheduled', 'Offered', 'Started', 'Accepted', 'Declined', 'Expired', 'Failed')");
                    table.ForeignKey(
                        name: "fk_proactive_runs_catalog_services_service_id",
                        column: x => x.service_id,
                        principalTable: "catalog_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_proactive_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_proactive_runs_tickets_source_ticket_id",
                        column: x => x.source_ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_proactive_runs_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_proactive_runs_users_person_id",
                        column: x => x.person_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_bundle_id",
                table: "service_requests",
                column: "bundle_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_proactive_run_id",
                table: "service_requests",
                column: "proactive_run_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_requests_bundle_part",
                table: "service_requests",
                sql: "NOT (is_bundle AND bundle_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_requests_bundle_stage",
                table: "service_requests",
                sql: "(bundle_id IS NULL) = (bundle_stage IS NULL) AND (bundle_stage IS NULL OR bundle_stage >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_requests_hold",
                table: "service_requests",
                sql: "hold IN ('None', 'Bundle', 'Payment')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_person_id",
                table: "proactive_runs",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_service_id",
                table: "proactive_runs",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_source_ticket_id",
                table: "proactive_runs",
                column: "source_ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_tenant_id_person_id_state",
                table: "proactive_runs",
                columns: new[] { "tenant_id", "person_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_tenant_id_service_id_trigger_key_person_id_o",
                table: "proactive_runs",
                columns: new[] { "tenant_id", "service_id", "trigger_key", "person_id", "occurrence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_tenant_id_state_due_on",
                table: "proactive_runs",
                columns: new[] { "tenant_id", "state", "due_on" });

            migrationBuilder.CreateIndex(
                name: "ix_proactive_runs_ticket_id",
                table: "proactive_runs",
                column: "ticket_id");

            migrationBuilder.AddForeignKey(
                name: "fk_service_requests_proactive_runs_proactive_run_id",
                table: "service_requests",
                column: "proactive_run_id",
                principalTable: "proactive_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_service_requests_tickets_bundle_id",
                table: "service_requests",
                column: "bundle_id",
                principalTable: "tickets",
                principalColumn: "id");

            migrationBuilder.EnableTenantIsolation("proactive_runs");

            // Task C5: the catalog job (bundles moving on, proactive services) finds tenants with
            // active services; only ids and slugs come back.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_catalog_job_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.catalog_services s WHERE s.tenant_id = t.id AND s.state = 'Active')
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_catalog_job_tenants();");
            migrationBuilder.DisableTenantIsolation("proactive_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_service_requests_proactive_runs_proactive_run_id",
                table: "service_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_service_requests_tickets_bundle_id",
                table: "service_requests");

            migrationBuilder.DropTable(
                name: "proactive_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_service_requests_bundle_id",
                table: "service_requests");

            migrationBuilder.DropIndex(
                name: "ix_service_requests_proactive_run_id",
                table: "service_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_requests_bundle_part",
                table: "service_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_requests_bundle_stage",
                table: "service_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_requests_hold",
                table: "service_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings");

            migrationBuilder.DropColumn(
                name: "held",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "bundle_id",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "bundle_optional",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "bundle_stage",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "hold",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "is_bundle",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "proactive_run_id",
                table: "service_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent')");
        }
    }
}
