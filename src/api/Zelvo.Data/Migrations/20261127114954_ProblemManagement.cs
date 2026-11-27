using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProblemManagement : Migration
    {
        /// <summary>Tenant-owned tables of problem management (module 16, task H1), all with row-level security.</summary>
        private static readonly string[] Tables =
        [
            "problems", "known_errors", "known_error_number_sequences", "problem_incidents", "problem_articles",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.CreateTable(
                name: "known_error_number_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_known_error_number_sequences", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_known_error_number_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "known_errors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    problem_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    symptoms_ar = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    symptoms_en = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    title_ar = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    title_en = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    workaround_ar = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    workaround_en = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_known_errors", x => x.id);
                    table.CheckConstraint("ck_known_errors_sequence", "sequence >= 1");
                    table.CheckConstraint("ck_known_errors_status", "status IN ('Active', 'Retired')");
                    table.CheckConstraint("ck_known_errors_title", "coalesce(length(btrim(title_en)), 0) + coalesce(length(btrim(title_ar)), 0) > 0");
                    table.CheckConstraint("ck_known_errors_workaround", "coalesce(length(btrim(workaround_en)), 0) + coalesce(length(btrim(workaround_ar)), 0) > 0");
                    table.ForeignKey(
                        name: "fk_known_errors_kb_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_known_errors_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_known_errors_tickets_problem_id",
                        column: x => x.problem_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_known_errors_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "problem_articles",
                columns: table => new
                {
                    problem_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_articles", x => new { x.problem_id, x.article_id });
                    table.ForeignKey(
                        name: "fk_problem_articles_kb_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_articles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_problem_articles_tickets_problem_id",
                        column: x => x.problem_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_articles_users_linked_by_id",
                        column: x => x.linked_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "problem_incidents",
                columns: table => new
                {
                    problem_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_incidents", x => new { x.problem_id, x.incident_id });
                    table.CheckConstraint("ck_problem_incidents_not_self", "problem_id <> incident_id");
                    table.ForeignKey(
                        name: "fk_problem_incidents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_problem_incidents_tickets_incident_id",
                        column: x => x.incident_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_incidents_tickets_problem_id",
                        column: x => x.problem_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_incidents_users_linked_by_id",
                        column: x => x.linked_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "problems",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    root_cause = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    five_whys = table.Column<string>(type: "jsonb", nullable: false),
                    fishbone = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problems", x => x.ticket_id);
                    table.CheckConstraint("ck_problems_fishbone", "jsonb_typeof(fishbone) = 'object'");
                    table.CheckConstraint("ck_problems_five_whys", "jsonb_typeof(five_whys) = 'object'");
                    table.CheckConstraint("ck_problems_phase", "phase IN ('Assessment', 'RootCauseAnalysis', 'KnownError')");
                    table.CheckConstraint("ck_problems_priority", "priority BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_problems_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_problems_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules",
                sql: "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.CreateIndex(
                name: "ix_known_errors_article_id",
                table: "known_errors",
                column: "article_id");

            migrationBuilder.CreateIndex(
                name: "ix_known_errors_created_by_id",
                table: "known_errors",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_known_errors_problem_id",
                table: "known_errors",
                column: "problem_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_known_errors_tenant_id_number",
                table: "known_errors",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_known_errors_tenant_id_sequence",
                table: "known_errors",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_problem_articles_article_id",
                table: "problem_articles",
                column: "article_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_articles_linked_by_id",
                table: "problem_articles",
                column: "linked_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_articles_tenant_id_article_id",
                table: "problem_articles",
                columns: new[] { "tenant_id", "article_id" });

            migrationBuilder.CreateIndex(
                name: "ix_problem_incidents_incident_id",
                table: "problem_incidents",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_incidents_linked_by_id",
                table: "problem_incidents",
                column: "linked_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_incidents_tenant_id_incident_id",
                table: "problem_incidents",
                columns: new[] { "tenant_id", "incident_id" });

            migrationBuilder.CreateIndex(
                name: "ix_problems_tenant_id",
                table: "problems",
                column: "tenant_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "known_error_number_sequences");

            migrationBuilder.DropTable(
                name: "known_errors");

            migrationBuilder.DropTable(
                name: "problem_articles");

            migrationBuilder.DropTable(
                name: "problem_incidents");

            migrationBuilder.DropTable(
                name: "problems");

            migrationBuilder.DropCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules",
                sql: "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");
        }
    }
}
