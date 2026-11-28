using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeManagement : Migration
    {
        /// <summary>Tenant-owned tables of change management (module 17, task H3), with row-level security.</summary>
        private static readonly string[] Tables = ["changes", "change_templates", "change_blackouts", "change_links"];

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

            migrationBuilder.DropCheckConstraint(
                name: "ck_approval_processes_stage",
                table: "approval_processes");

            migrationBuilder.CreateTable(
                name: "change_blackouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_blackouts", x => x.id);
                    table.CheckConstraint("ck_change_blackouts_name", "coalesce(length(btrim(name_en)), 0) + coalesce(length(btrim(name_ar)), 0) > 0");
                    table.CheckConstraint("ck_change_blackouts_window", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "fk_change_blackouts_configuration_items_configuration_item_id",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_change_blackouts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change_links",
                columns: table => new
                {
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    linked_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_links", x => new { x.change_id, x.ticket_id });
                    table.CheckConstraint("ck_change_links_kind", "kind IN ('Fixes', 'CausedBy')");
                    table.CheckConstraint("ck_change_links_not_self", "change_id <> ticket_id");
                    table.ForeignKey(
                        name: "fk_change_links_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_links_tickets_change_id",
                        column: x => x.change_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_change_links_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_change_links_users_linked_by_id",
                        column: x => x.linked_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "change_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    implementation_plan = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    test_plan = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    backout_plan = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    tasks = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_templates", x => x.id);
                    table.CheckConstraint("ck_change_templates_name", "coalesce(length(btrim(name_en)), 0) + coalesce(length(btrim(name_ar)), 0) > 0");
                    table.CheckConstraint("ck_change_templates_tasks", "jsonb_typeof(tasks) = 'array'");
                    table.ForeignKey(
                        name: "fk_change_templates_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_change_templates_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_change_templates_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "changes",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    phase = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    risk_answers = table.Column<string>(type: "jsonb", nullable: false),
                    risk_score = table.Column<int>(type: "integer", nullable: true),
                    risk_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    implementation_plan = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    test_plan = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    backout_plan = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    planned_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    planned_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actual_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actual_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    review_notes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    reviewed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_changes", x => x.ticket_id);
                    table.CheckConstraint("ck_changes_closure", "closure IN ('Completed', 'Rejected', 'Cancelled')");
                    table.CheckConstraint("ck_changes_kind", "kind IN ('Standard', 'Normal', 'Emergency')");
                    table.CheckConstraint("ck_changes_outcome", "outcome IN ('Successful', 'SuccessfulWithProblems', 'Failed', 'BackedOut')");
                    table.CheckConstraint("ck_changes_phase", "phase IN ('Planning', 'Authorisation', 'Scheduled', 'Implementation')");
                    table.CheckConstraint("ck_changes_risk_answers", "jsonb_typeof(risk_answers) = 'object'");
                    table.CheckConstraint("ck_changes_risk_level", "risk_level IN ('Low', 'Medium', 'High')");
                    table.CheckConstraint("ck_changes_window", "planned_start IS NULL OR planned_end IS NULL OR planned_end > planned_start");
                    table.ForeignKey(
                        name: "fk_changes_change_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "change_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_changes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_changes_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_changes_users_reviewed_by_id",
                        column: x => x.reviewed_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules",
                sql: "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom', 'Change')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom', 'Change')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom', 'Change')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom', 'Change')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom', 'Change')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom', 'Change')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_approval_processes_stage",
                table: "approval_processes",
                sql: "stage IN ('BeforeAssignment', 'AfterFulfilment', 'ChangeAuthorisation')");

            migrationBuilder.CreateIndex(
                name: "ix_change_blackouts_configuration_item_id",
                table: "change_blackouts",
                column: "configuration_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_blackouts_tenant_id_starts_at",
                table: "change_blackouts",
                columns: new[] { "tenant_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_links_linked_by_id",
                table: "change_links",
                column: "linked_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_links_tenant_id_ticket_id",
                table: "change_links",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_links_ticket_id",
                table: "change_links",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_templates_category_id",
                table: "change_templates",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_templates_group_id",
                table: "change_templates",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_templates_tenant_id",
                table: "change_templates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_changes_reviewed_by_id",
                table: "changes",
                column: "reviewed_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_changes_template_id",
                table: "changes",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_changes_tenant_id_planned_start",
                table: "changes",
                columns: new[] { "tenant_id", "planned_start" });

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
                name: "change_blackouts");

            migrationBuilder.DropTable(
                name: "change_links");

            migrationBuilder.DropTable(
                name: "changes");

            migrationBuilder.DropTable(
                name: "change_templates");

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

            migrationBuilder.DropCheckConstraint(
                name: "ck_approval_processes_stage",
                table: "approval_processes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules",
                sql: "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_approval_processes_stage",
                table: "approval_processes",
                sql: "stage IN ('BeforeAssignment', 'AfterFulfilment')");
        }
    }
}
