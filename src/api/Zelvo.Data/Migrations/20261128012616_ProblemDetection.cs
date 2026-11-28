using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <summary>
    /// Task H2 (module 16): suggested problems from similar incidents and trends, with the
    /// incidents they name, under row-level security, and the function the detection job uses to
    /// find tenants that have detection on.
    /// </summary>
    public partial class ProblemDetection : Migration
    {
        private static readonly string[] Tables = ["problem_suggestions", "problem_suggestion_incidents"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "problem_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    confidence = table.Column<int>(type: "integer", nullable: true),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    dimension = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    incident_count = table.Column<int>(type: "integer", nullable: false),
                    baseline = table.Column<double>(type: "double precision", nullable: true),
                    ai_call_id = table.Column<long>(type: "bigint", nullable: true),
                    problem_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismiss_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_suggestions", x => x.id);
                    table.CheckConstraint("ck_problem_suggestions_accepted", "(status = 'Accepted') = (problem_id IS NOT NULL)");
                    table.CheckConstraint("ck_problem_suggestions_confidence", "confidence IS NULL OR confidence BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_problem_suggestions_count", "incident_count >= 0");
                    table.CheckConstraint("ck_problem_suggestions_dimension", "dimension IS NULL OR dimension IN ('Category', 'ConfigurationItem', 'Service')");
                    table.CheckConstraint("ck_problem_suggestions_dismissed", "status <> 'Dismissed' OR length(btrim(dismiss_reason)) > 0");
                    table.CheckConstraint("ck_problem_suggestions_kind", "kind IN ('SimilarIncidents', 'Trend')");
                    table.CheckConstraint("ck_problem_suggestions_language", "language IS NULL OR language IN ('en', 'ar')");
                    table.CheckConstraint("ck_problem_suggestions_status", "status IN ('Open', 'Accepted', 'Dismissed', 'NotConfirmed')");
                    table.CheckConstraint("ck_problem_suggestions_trend", "(kind = 'Trend') = (dimension IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_problem_suggestions_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_problem_suggestions_configuration_items_configuration_item_",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_suggestions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_problem_suggestions_tickets_problem_id",
                        column: x => x.problem_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_problem_suggestions_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "problem_suggestion_incidents",
                columns: table => new
                {
                    suggestion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_suggestion_incidents", x => new { x.suggestion_id, x.incident_id });
                    table.ForeignKey(
                        name: "fk_problem_suggestion_incidents_problem_suggestions_suggestion",
                        column: x => x.suggestion_id,
                        principalTable: "problem_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_suggestion_incidents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_problem_suggestion_incidents_tickets_incident_id",
                        column: x => x.incident_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestion_incidents_incident_id",
                table: "problem_suggestion_incidents",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestion_incidents_tenant_id_incident_id",
                table: "problem_suggestion_incidents",
                columns: new[] { "tenant_id", "incident_id" });

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_category_id",
                table: "problem_suggestions",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_configuration_item_id",
                table: "problem_suggestions",
                column: "configuration_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_decided_by_id",
                table: "problem_suggestions",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_problem_id",
                table: "problem_suggestions",
                column: "problem_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_tenant_id_kind_dimension_category_id_co",
                table: "problem_suggestions",
                columns: new[] { "tenant_id", "kind", "dimension", "category_id", "configuration_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_tenant_id_status_created_at",
                table: "problem_suggestions",
                columns: new[] { "tenant_id", "status", "created_at" });

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The detection job looks at active tenants unless both kinds of detection are off.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_problem_detection_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active' AND NOT EXISTS (
                        SELECT 1 FROM public.tenant_settings s
                        WHERE s.tenant_id = t.id AND s.key = 'problems.detection'
                          AND NOT coalesce((s.value ->> 'similarEnabled')::boolean, true)
                          AND NOT coalesce((s.value ->> 'trendsEnabled')::boolean, true))
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_problem_detection_tenants();");
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "problem_suggestion_incidents");

            migrationBuilder.DropTable(
                name: "problem_suggestions");
        }
    }
}
