using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_api_client_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mcp_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approval_required = table.Column<bool>(type: "boolean", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_actions", x => x.id);
                    table.CheckConstraint("ck_mcp_actions_expiry", "NOT approval_required OR expires_at IS NOT NULL");
                    table.CheckConstraint("ck_mcp_actions_status", "status IN ('Pending', 'Rejected', 'Expired', 'Succeeded', 'Failed')");
                    table.CheckConstraint("ck_mcp_actions_tool", "tool ~ '^[a-z][a-z0-9_]*$'");
                    table.ForeignKey(
                        name: "fk_mcp_actions_api_clients_api_client_id",
                        column: x => x.api_client_id,
                        principalTable: "api_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mcp_actions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mcp_actions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mcp_actions_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mcp_agents",
                columns: table => new
                {
                    api_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tools = table.Column<string[]>(type: "text[]", nullable: false),
                    tools_without_approval = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_agents", x => x.api_client_id);
                    table.ForeignKey(
                        name: "fk_mcp_agents_api_clients_api_client_id",
                        column: x => x.api_client_id,
                        principalTable: "api_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mcp_agents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    auth_header = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    secret_ciphertext = table.Column<string>(type: "text", nullable: true),
                    secret_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tools = table.Column<string>(type: "jsonb", nullable: false),
                    tools_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_servers", x => x.id);
                    table.CheckConstraint("ck_mcp_servers_auth_header", "auth_header IS NULL OR auth_header ~ '^[A-Za-z][A-Za-z0-9-]*$'");
                    table.CheckConstraint("ck_mcp_servers_status", "status IN ('Active', 'Disabled')");
                    table.CheckConstraint("ck_mcp_servers_url", "url ~ '^https?://'");
                    table.ForeignKey(
                        name: "fk_mcp_servers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_created_by_api_client_id",
                table: "tickets",
                column: "created_by_api_client_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent')");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_actions_api_client_id_requested_at",
                table: "mcp_actions",
                columns: new[] { "api_client_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_actions_decided_by_id",
                table: "mcp_actions",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_actions_tenant_id_status_requested_at",
                table: "mcp_actions",
                columns: new[] { "tenant_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_actions_ticket_id",
                table: "mcp_actions",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_agents_tenant_id",
                table: "mcp_agents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_servers_tenant_id_status",
                table: "mcp_servers",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_api_clients_created_by_api_client_id",
                table: "tickets",
                column: "created_by_api_client_id",
                principalTable: "api_clients",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            foreach (var table in new[] { "mcp_agents", "mcp_actions", "mcp_servers" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "mcp_agents", "mcp_actions", "mcp_servers" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_api_clients_created_by_api_client_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "mcp_actions");

            migrationBuilder.DropTable(
                name: "mcp_agents");

            migrationBuilder.DropTable(
                name: "mcp_servers");

            migrationBuilder.DropIndex(
                name: "ix_tickets_created_by_api_client_id",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "created_by_api_client_id",
                table: "tickets");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api')");
        }
    }
}
