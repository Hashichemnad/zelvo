using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Fulfilment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "completion_minutes",
                table: "service_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "escalations",
                table: "service_requests",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<int>(
                name: "ownership_minutes",
                table: "service_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "request_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<int>(type: "integer", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_minutes = table.Column<int>(type: "integer", nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_request_tasks", x => x.id);
                    table.CheckConstraint("ck_request_tasks_stage", "stage >= 0");
                    table.CheckConstraint("ck_request_tasks_state", "state IN ('Waiting', 'Open', 'InProgress', 'Done', 'Cancelled')");
                    table.CheckConstraint("ck_request_tasks_target", "target_minutes IS NULL OR target_minutes > 0");
                    table.ForeignKey(
                        name: "fk_request_tasks_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_tasks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_tasks_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_request_tasks_users_assignee_id",
                        column: x => x.assignee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_tasks_users_completed_by_id",
                        column: x => x.completed_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_requests_completion",
                table: "service_requests",
                sql: "completion_minutes IS NULL OR completion_minutes > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_requests_ownership",
                table: "service_requests",
                sql: "ownership_minutes IS NULL OR ownership_minutes > 0");

            migrationBuilder.CreateIndex(
                name: "ix_request_tasks_assignee_id",
                table: "request_tasks",
                column: "assignee_id");

            migrationBuilder.CreateIndex(
                name: "ix_request_tasks_completed_by_id",
                table: "request_tasks",
                column: "completed_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_request_tasks_group_id",
                table: "request_tasks",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_request_tasks_tenant_id_assignee_id_state",
                table: "request_tasks",
                columns: new[] { "tenant_id", "assignee_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_request_tasks_tenant_id_group_id_state",
                table: "request_tasks",
                columns: new[] { "tenant_id", "group_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_request_tasks_ticket_id_key",
                table: "request_tasks",
                columns: new[] { "ticket_id", "key" },
                unique: true);
            migrationBuilder.EnableTenantIsolation("request_tasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantIsolation("request_tasks");
            migrationBuilder.DropTable(
                name: "request_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_requests_completion",
                table: "service_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_requests_ownership",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "completion_minutes",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "escalations",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "ownership_minutes",
                table: "service_requests");
        }
    }
}
