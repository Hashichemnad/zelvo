using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Workflows : Migration
    {
        private static readonly string[] Tables = ["workflow_rules", "approval_processes", "approval_decisions"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "awaiting_approval",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "approval_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    levels = table.Column<string>(type: "jsonb", nullable: false),
                    current_level = table.Column<int>(type: "integer", nullable: false),
                    engine_instance_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    engine_bookmark_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    resolution_notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    started_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_processes", x => x.id);
                    table.CheckConstraint("ck_approval_processes_stage", "stage IN ('BeforeAssignment', 'AfterFulfilment')");
                    table.CheckConstraint("ck_approval_processes_state", "state IN ('Pending', 'Approved', 'Rejected', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_approval_processes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approval_processes_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_approval_processes_users_started_by_id",
                        column: x => x.started_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "workflow_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    trigger = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    record_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    match = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    stop_processing = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_rules", x => x.id);
                    table.CheckConstraint("ck_workflow_rules_match", "match IN ('All', 'Any')");
                    table.CheckConstraint("ck_workflow_rules_record_type", "record_type IS NULL OR record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");
                    table.CheckConstraint("ck_workflow_rules_trigger", "trigger IN ('TicketCreated', 'StateChanged', 'AssignmentChanged', 'ApprovalDecided')");
                    table.ForeignKey(
                        name: "fk_workflow_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workflow_rules_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "approval_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    approver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    decided_via = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_decisions", x => x.id);
                    table.CheckConstraint("ck_approval_decisions_reason", "state <> 'Rejected' OR comment IS NOT NULL");
                    table.CheckConstraint("ck_approval_decisions_state", "state IN ('Pending', 'Approved', 'Rejected', 'NotNeeded')");
                    table.ForeignKey(
                        name: "fk_approval_decisions_approval_processes_process_id",
                        column: x => x.process_id,
                        principalTable: "approval_processes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_approval_decisions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approval_decisions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_approval_decisions_users_approver_id",
                        column: x => x.approver_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_decisions_approver_id",
                table: "approval_decisions",
                column: "approver_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_decisions_process_id_level_approver_id",
                table: "approval_decisions",
                columns: new[] { "process_id", "level", "approver_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_approval_decisions_tenant_id_approver_id_state",
                table: "approval_decisions",
                columns: new[] { "tenant_id", "approver_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_decisions_ticket_id",
                table: "approval_decisions",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_processes_started_by_id",
                table: "approval_processes",
                column: "started_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_processes_tenant_id_ticket_id",
                table: "approval_processes",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_processes_ticket_id",
                table: "approval_processes",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_rules_created_by_id",
                table: "workflow_rules",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_rules_tenant_id_trigger_position",
                table: "workflow_rules",
                columns: new[] { "tenant_id", "trigger", "position" });
            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Elsa Workflows keeps the approval engine's state in its own schema (task C3). Its
            // tables are created by Elsa's own migrations in the migrate step, which then calls
            // this function: every Elsa table gets a tenant column that the database fills from the
            // connection's tenant, forced row-level security, and the application role's grants.
            // Definitions written by code (shared by all tenants) may have no tenant.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_secure_elsa_tables() RETURNS void
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    t text;
                    r text;
                    shared constant text[] := ARRAY['WorkflowDefinitions', 'Triggers', 'WorkflowDefinitionRegistryGenerations', 'KeyValuePairs'];
                BEGIN
                    IF to_regnamespace('"Elsa"') IS NULL THEN
                        RETURN;
                    END IF;
                    FOR t IN
                        SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'Elsa' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
                    LOOP
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'Elsa' AND table_name = t AND column_name = 'zelvo_tenant_id') THEN
                            EXECUTE format('ALTER TABLE "Elsa".%I ADD COLUMN zelvo_tenant_id uuid DEFAULT zelvo_current_tenant_id()', t);
                            EXECUTE format('CREATE INDEX %I ON "Elsa".%I (zelvo_tenant_id)', 'ix_zelvo_' || lower(t) || '_tenant', t);
                        END IF;
                        EXECUTE format('ALTER TABLE "Elsa".%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE "Elsa".%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON "Elsa".%I', t);
                        IF t = ANY (shared) THEN
                            EXECUTE format('CREATE POLICY tenant_isolation ON "Elsa".%I USING (zelvo_tenant_id IS NULL OR zelvo_tenant_id = zelvo_current_tenant_id()) WITH CHECK (zelvo_tenant_id IS NULL OR zelvo_tenant_id = zelvo_current_tenant_id())', t);
                        ELSE
                            EXECUTE format('CREATE POLICY tenant_isolation ON "Elsa".%I USING (zelvo_tenant_id = zelvo_current_tenant_id()) WITH CHECK (zelvo_tenant_id = zelvo_current_tenant_id())', t);
                        END IF;
                    END LOOP;
                    -- The application role is whoever may read public.tenants, apart from the owner.
                    FOR r IN
                        SELECT DISTINCT grantee FROM information_schema.role_table_grants
                        WHERE table_schema = 'public' AND table_name = 'tenants' AND privilege_type = 'SELECT'
                          AND grantee <> current_user AND grantee <> 'PUBLIC'
                    LOOP
                        EXECUTE format('GRANT USAGE ON SCHEMA "Elsa" TO %I', r);
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "Elsa" TO %I', r);
                        EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA "Elsa" TO %I', r);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS zelvo_secure_elsa_tables();");
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "approval_decisions");

            migrationBuilder.DropTable(
                name: "workflow_rules");

            migrationBuilder.DropTable(
                name: "approval_processes");

            migrationBuilder.DropColumn(
                name: "awaiting_approval",
                table: "tickets");
        }
    }
}
