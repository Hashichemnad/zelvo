using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Tickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_number_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_number_sequences", x => new { x.tenant_id, x.type });
                    table.CheckConstraint("ck_ticket_number_sequences_type", "type IN ('Incident')");
                    table.ForeignKey(
                        name: "fk_ticket_number_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    follow_up_of_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_hold_reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    resolution_notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    reopen_count = table.Column<int>(type: "integer", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.CheckConstraint("ck_tickets_channel", "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api')");
                    table.CheckConstraint("ck_tickets_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_tickets_on_hold_reason", "(state = 'OnHold') = (on_hold_reason IS NOT NULL)");
                    table.CheckConstraint("ck_tickets_state", "state IN ('New', 'Assigned', 'InProgress', 'OnHold', 'Resolved', 'Closed', 'Reopened')");
                    table.CheckConstraint("ck_tickets_type", "type IN ('Incident')");
                    table.ForeignKey(
                        name: "fk_tickets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_tickets_follow_up_of_id",
                        column: x => x.follow_up_of_id,
                        principalTable: "tickets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_tickets_parent_id",
                        column: x => x.parent_id,
                        principalTable: "tickets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_users_assignee_id",
                        column: x => x.assignee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_users_requester_id",
                        column: x => x.requester_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    impact = table.Column<int>(type: "integer", nullable: false),
                    urgency = table.Column<int>(type: "integer", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_major = table.Column<bool>(type: "boolean", nullable: false),
                    major_declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    major_declared_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.ticket_id);
                    table.CheckConstraint("ck_incidents_levels", "impact BETWEEN 1 AND 9 AND urgency BETWEEN 1 AND 9 AND priority BETWEEN 1 AND 9");
                    table.CheckConstraint("ck_incidents_major", "NOT is_major OR major_declared_at IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_incidents_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incidents_users_major_declared_by_id",
                        column: x => x.major_declared_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "major_incident_updates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    posted_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    body_ar = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    body_en = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_major_incident_updates", x => x.id);
                    table.CheckConstraint("ck_major_incident_updates_audience", "audience IN ('Internal', 'Requesters')");
                    table.ForeignKey(
                        name: "fk_major_incident_updates_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_major_incident_updates_users_posted_by_id",
                        column: x => x.posted_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    scan_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scan_result = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    scan_attempts = table.Column<int>(type: "integer", nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    uploaded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_attachments", x => x.id);
                    table.CheckConstraint("ck_ticket_attachments_scan_status", "scan_status IN ('Pending', 'Clean', 'Infected', 'NotScanned')");
                    table.CheckConstraint("ck_ticket_attachments_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_ticket_attachments_size", "size >= 0");
                    table.ForeignKey(
                        name: "fk_ticket_attachments_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_attachments_users_uploaded_by_id",
                        column: x => x.uploaded_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_attachment_contents",
                columns: table => new
                {
                    attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_attachment_contents", x => x.attachment_id);
                    table.ForeignKey(
                        name: "fk_ticket_attachment_contents_ticket_attachments_attachment_id",
                        column: x => x.attachment_id,
                        principalTable: "ticket_attachments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_major_declared_by_id",
                table: "incidents",
                column: "major_declared_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_tenant_id_is_major",
                table: "incidents",
                columns: new[] { "tenant_id", "is_major" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_tenant_id_priority",
                table: "incidents",
                columns: new[] { "tenant_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "ix_major_incident_updates_posted_by_id",
                table: "major_incident_updates",
                column: "posted_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_major_incident_updates_tenant_id_ticket_id_posted_at",
                table: "major_incident_updates",
                columns: new[] { "tenant_id", "ticket_id", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_major_incident_updates_ticket_id",
                table: "major_incident_updates",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachment_contents_tenant_id",
                table: "ticket_attachment_contents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_pending",
                table: "ticket_attachments",
                columns: new[] { "tenant_id", "uploaded_at" },
                filter: "scan_status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_tenant_id_ticket_id",
                table: "ticket_attachments",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_ticket_id",
                table: "ticket_attachments",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_uploaded_by_id",
                table: "ticket_attachments",
                column: "uploaded_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_assignee_id",
                table: "tickets",
                column: "assignee_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_created_by_id",
                table: "tickets",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_follow_up_of_id",
                table: "tickets",
                column: "follow_up_of_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_parent_id",
                table: "tickets",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_requester_id",
                table: "tickets",
                column: "requester_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_assignee_id",
                table: "tickets",
                columns: new[] { "tenant_id", "assignee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_created_at",
                table: "tickets",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_number",
                table: "tickets",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_parent_id",
                table: "tickets",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_requester_id_created_at",
                table: "tickets",
                columns: new[] { "tenant_id", "requester_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_state_resolved_at",
                table: "tickets",
                columns: new[] { "tenant_id", "state", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_type_sequence",
                table: "tickets",
                columns: new[] { "tenant_id", "type", "sequence" },
                unique: true);

            foreach (var table in new[] { "tickets", "incidents", "ticket_number_sequences", "ticket_attachments", "ticket_attachment_contents", "major_incident_updates" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Worker jobs find which tenants have work without seeing any tenant's rows: only ids
            // and slugs come back. Each job then works with that tenant set, under row-level security.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_tenants_with_resolved_tickets()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.tickets k WHERE k.tenant_id = t.id AND k.state = 'Resolved')
                $$;

                CREATE FUNCTION zelvo_tenants_with_pending_scans()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.ticket_attachments a WHERE a.tenant_id = t.id AND a.scan_status = 'Pending')
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_tenants_with_pending_scans(); DROP FUNCTION zelvo_tenants_with_resolved_tickets();");
            foreach (var table in new[] { "tickets", "incidents", "ticket_number_sequences", "ticket_attachments", "ticket_attachment_contents", "major_incident_updates" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "major_incident_updates");

            migrationBuilder.DropTable(
                name: "ticket_attachment_contents");

            migrationBuilder.DropTable(
                name: "ticket_number_sequences");

            migrationBuilder.DropTable(
                name: "ticket_attachments");

            migrationBuilder.DropTable(
                name: "tickets");
        }
    }
}
