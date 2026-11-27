using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <summary>
    /// Task K4 (module 21): agents' daily wellbeing figures, groups' thresholds and alerts, with
    /// row-level security, and the function the wellbeing job uses to find tenants that have it on.
    /// </summary>
    public partial class Wellbeing : Migration
    {
        private static readonly string[] Tables = ["wellbeing_days", "wellbeing_thresholds", "wellbeing_alerts"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wellbeing_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false),
                    threshold = table.Column<int>(type: "integer", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wellbeing_alerts", x => x.id);
                    table.CheckConstraint("ck_wellbeing_alerts_kind", "kind IN ('Workload', 'NewAssignments', 'AfterHours', 'Difficult')");
                    table.ForeignKey(
                        name: "fk_wellbeing_alerts_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_wellbeing_alerts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wellbeing_alerts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wellbeing_days",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    open_assigned = table.Column<int>(type: "integer", nullable: false),
                    weighted_load = table.Column<int>(type: "integer", nullable: false),
                    new_assignments = table.Column<int>(type: "integer", nullable: false),
                    actions = table.Column<int>(type: "integer", nullable: false),
                    after_hours_actions = table.Column<int>(type: "integer", nullable: false),
                    after_hours_hours = table.Column<int>(type: "integer", nullable: false),
                    handled_tickets = table.Column<int>(type: "integer", nullable: false),
                    difficult_tickets = table.Column<int>(type: "integer", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wellbeing_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_wellbeing_days_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wellbeing_days_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wellbeing_thresholds",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_weighted_load = table.Column<int>(type: "integer", nullable: true),
                    max_new_per_day = table.Column<int>(type: "integer", nullable: true),
                    max_after_hours_hours = table.Column<int>(type: "integer", nullable: true),
                    max_difficult_percent = table.Column<int>(type: "integer", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wellbeing_thresholds", x => x.group_id);
                    table.CheckConstraint("ck_wellbeing_thresholds_difficult", "max_difficult_percent IS NULL OR max_difficult_percent BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_wellbeing_thresholds_hours", "max_after_hours_hours IS NULL OR max_after_hours_hours BETWEEN 0 AND 168");
                    table.CheckConstraint("ck_wellbeing_thresholds_load", "max_weighted_load IS NULL OR max_weighted_load BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_wellbeing_thresholds_new", "max_new_per_day IS NULL OR max_new_per_day BETWEEN 1 AND 500");
                    table.ForeignKey(
                        name: "fk_wellbeing_thresholds_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_wellbeing_thresholds_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wellbeing_thresholds_users_updated_by_id",
                        column: x => x.updated_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_alerts_group_id",
                table: "wellbeing_alerts",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_alerts_tenant_id_group_id_raised_at",
                table: "wellbeing_alerts",
                columns: new[] { "tenant_id", "group_id", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_alerts_tenant_id_user_id_group_id_kind",
                table: "wellbeing_alerts",
                columns: new[] { "tenant_id", "user_id", "group_id", "kind" },
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_alerts_user_id",
                table: "wellbeing_alerts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_days_tenant_id_day",
                table: "wellbeing_days",
                columns: new[] { "tenant_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_days_tenant_id_user_id_day",
                table: "wellbeing_days",
                columns: new[] { "tenant_id", "user_id", "day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_days_user_id",
                table: "wellbeing_days",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_thresholds_tenant_id",
                table: "wellbeing_thresholds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_thresholds_updated_by_id",
                table: "wellbeing_thresholds",
                column: "updated_by_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The wellbeing job looks only at tenants that turned wellbeing on.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_wellbeing_job_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active' AND EXISTS (
                        SELECT 1 FROM public.tenant_settings s
                        WHERE s.tenant_id = t.id AND s.key = 'wellbeing.state' AND (s.value ->> 'enabled')::boolean)
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_wellbeing_job_tenants();");
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "wellbeing_alerts");

            migrationBuilder.DropTable(
                name: "wellbeing_days");

            migrationBuilder.DropTable(
                name: "wellbeing_thresholds");
        }
    }
}
