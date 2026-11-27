using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomRecordTypes : Migration
    {
        /// <summary>Tenant-owned tables of custom record types (module 20, task K2), with row-level security.</summary>
        private static readonly string[] Tables = ["record_types", "custom_records"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_type_sequence",
                table: "tickets");

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

            migrationBuilder.DropIndex(
                name: "ix_form_bindings_tenant_id_record_type_category_id",
                table: "form_bindings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.AddColumn<Guid>(
                name: "custom_record_type_id",
                table: "workflow_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "record_type_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "custom_record_type_id",
                table: "sla_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "custom_record_type_id",
                table: "form_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "record_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    number_prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    states = table.Column<string>(type: "jsonb", nullable: false),
                    portal_entry = table.Column<bool>(type: "boolean", nullable: false),
                    default_priority = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record_types", x => x.id);
                    table.CheckConstraint("ck_record_types_key", "key ~ '^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$'");
                    table.CheckConstraint("ck_record_types_last_number", "last_number >= 0");
                    table.CheckConstraint("ck_record_types_prefix", "number_prefix ~ '^[A-Z][A-Z0-9]{0,9}$'");
                    table.CheckConstraint("ck_record_types_priority", "default_priority BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_record_types_states", "jsonb_typeof(states) = 'array'");
                    table.ForeignKey(
                        name: "fk_record_types_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_records",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custom_records", x => x.ticket_id);
                    table.CheckConstraint("ck_custom_records_priority", "priority BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_custom_records_record_types_record_type_id",
                        column: x => x.record_type_id,
                        principalTable: "record_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_custom_records_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_custom_records_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_rules_custom_record_type_id",
                table: "workflow_rules",
                column: "custom_record_type_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules",
                sql: "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_record_type_id",
                table: "tickets",
                column: "record_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_type_record_type_id_sequence",
                table: "tickets",
                columns: new[] { "tenant_id", "type", "record_type_id", "sequence" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_record_type",
                table: "tickets",
                sql: "(type = 'Custom') = (record_type_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_custom_record_type_id",
                table: "sla_policies",
                column: "custom_record_type_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.CreateIndex(
                name: "ix_form_bindings_custom_record_type_id",
                table: "form_bindings",
                column: "custom_record_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_bindings_tenant_id_record_type_custom_record_type_id_c",
                table: "form_bindings",
                columns: new[] { "tenant_id", "record_type", "custom_record_type_id", "category_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem', 'Custom')");

            migrationBuilder.CreateIndex(
                name: "ix_custom_records_record_type_id",
                table: "custom_records",
                column: "record_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_custom_records_tenant_id_record_type_id_state_key",
                table: "custom_records",
                columns: new[] { "tenant_id", "record_type_id", "state_key" });

            migrationBuilder.CreateIndex(
                name: "ix_record_types_tenant_id_key",
                table: "record_types",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_record_types_tenant_id_number_prefix",
                table: "record_types",
                columns: new[] { "tenant_id", "number_prefix" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_form_bindings_record_types_custom_record_type_id",
                table: "form_bindings",
                column: "custom_record_type_id",
                principalTable: "record_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_sla_policies_record_types_custom_record_type_id",
                table: "sla_policies",
                column: "custom_record_type_id",
                principalTable: "record_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_record_types_record_type_id",
                table: "tickets",
                column: "record_type_id",
                principalTable: "record_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_rules_record_types_custom_record_type_id",
                table: "workflow_rules",
                column: "custom_record_type_id",
                principalTable: "record_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

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

            migrationBuilder.DropForeignKey(
                name: "fk_form_bindings_record_types_custom_record_type_id",
                table: "form_bindings");

            migrationBuilder.DropForeignKey(
                name: "fk_sla_policies_record_types_custom_record_type_id",
                table: "sla_policies");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_record_types_record_type_id",
                table: "tickets");

            migrationBuilder.DropForeignKey(
                name: "fk_workflow_rules_record_types_custom_record_type_id",
                table: "workflow_rules");

            migrationBuilder.DropTable(
                name: "custom_records");

            migrationBuilder.DropTable(
                name: "record_types");

            migrationBuilder.DropIndex(
                name: "ix_workflow_rules_custom_record_type_id",
                table: "workflow_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules");

            migrationBuilder.DropIndex(
                name: "ix_tickets_record_type_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_type_record_type_id_sequence",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_record_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropIndex(
                name: "ix_sla_policies_custom_record_type_id",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_record_type",
                table: "happiness_ratings");

            migrationBuilder.DropIndex(
                name: "ix_form_bindings_custom_record_type_id",
                table: "form_bindings");

            migrationBuilder.DropIndex(
                name: "ix_form_bindings_tenant_id_record_type_custom_record_type_id_c",
                table: "form_bindings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.DropColumn(
                name: "custom_record_type_id",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "record_type_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "custom_record_type_id",
                table: "sla_policies");

            migrationBuilder.DropColumn(
                name: "custom_record_type_id",
                table: "form_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_rules_record_type",
                table: "workflow_rules",
                sql: "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_type_sequence",
                table: "tickets",
                columns: new[] { "tenant_id", "type", "sequence" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_form_bindings_tenant_id_record_type_category_id",
                table: "form_bindings",
                columns: new[] { "tenant_id", "record_type", "category_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion', 'Problem')");
        }
    }
}
