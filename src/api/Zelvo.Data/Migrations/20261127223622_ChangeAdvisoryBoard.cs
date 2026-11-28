using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeAdvisoryBoard : Migration
    {
        /// <summary>Tenant-owned tables of the change advisory board (module 17, task H4), with row-level security.</summary>
        private static readonly string[] Tables = ["change_boards", "change_board_members", "cab_meetings", "cab_meeting_members", "cab_agenda_items", "cab_votes"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cab_meetings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    chair_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    minutes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cab_meetings", x => x.id);
                    table.CheckConstraint("ck_cab_meetings_state", "state IN ('Planned', 'Held', 'Cancelled')");
                    table.CheckConstraint("ck_cab_meetings_title", "coalesce(length(btrim(title_en)), 0) + coalesce(length(btrim(title_ar)), 0) > 0");
                    table.CheckConstraint("ck_cab_meetings_window", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "fk_cab_meetings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cab_meetings_users_chair_id",
                        column: x => x.chair_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cab_meetings_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "change_boards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    chair_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deputy_chair_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decide_within_hours = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_boards", x => x.id);
                    table.CheckConstraint("ck_change_boards_hours", "decide_within_hours BETWEEN 1 AND 168");
                    table.CheckConstraint("ck_change_boards_kind", "kind IN ('Advisory', 'Emergency')");
                    table.ForeignKey(
                        name: "fk_change_boards_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_boards_users_chair_id",
                        column: x => x.chair_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_change_boards_users_deputy_chair_id",
                        column: x => x.deputy_chair_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cab_agenda_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meeting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    minutes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cab_agenda_items", x => x.id);
                    table.CheckConstraint("ck_cab_agenda_items_decision", "decision IN ('Approved', 'Rejected', 'Deferred')");
                    table.ForeignKey(
                        name: "fk_cab_agenda_items_cab_meetings_meeting_id",
                        column: x => x.meeting_id,
                        principalTable: "cab_meetings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cab_agenda_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cab_agenda_items_tickets_change_id",
                        column: x => x.change_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cab_agenda_items_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cab_meeting_members",
                columns: table => new
                {
                    meeting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cab_meeting_members", x => new { x.meeting_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_cab_meeting_members_cab_meetings_meeting_id",
                        column: x => x.meeting_id,
                        principalTable: "cab_meetings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cab_meeting_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cab_meeting_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "change_board_members",
                columns: table => new
                {
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_board_members", x => new { x.board_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_change_board_members_change_boards_board_id",
                        column: x => x.board_id,
                        principalTable: "change_boards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_change_board_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_board_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cab_votes",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    choice = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    voted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cab_votes", x => new { x.item_id, x.user_id });
                    table.CheckConstraint("ck_cab_votes_choice", "choice IN ('Approve', 'Reject', 'Abstain')");
                    table.ForeignKey(
                        name: "fk_cab_votes_cab_agenda_items_item_id",
                        column: x => x.item_id,
                        principalTable: "cab_agenda_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cab_votes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cab_votes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cab_agenda_items_change_id",
                table: "cab_agenda_items",
                column: "change_id");

            migrationBuilder.CreateIndex(
                name: "ix_cab_agenda_items_decided_by_id",
                table: "cab_agenda_items",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_cab_agenda_items_meeting_id_change_id",
                table: "cab_agenda_items",
                columns: new[] { "meeting_id", "change_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cab_agenda_items_tenant_id_change_id",
                table: "cab_agenda_items",
                columns: new[] { "tenant_id", "change_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cab_meeting_members_tenant_id_user_id",
                table: "cab_meeting_members",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cab_meeting_members_user_id",
                table: "cab_meeting_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cab_meetings_chair_id",
                table: "cab_meetings",
                column: "chair_id");

            migrationBuilder.CreateIndex(
                name: "ix_cab_meetings_created_by_id",
                table: "cab_meetings",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_cab_meetings_tenant_id_starts_at",
                table: "cab_meetings",
                columns: new[] { "tenant_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cab_votes_tenant_id_user_id",
                table: "cab_votes",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cab_votes_user_id",
                table: "cab_votes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_board_members_tenant_id_user_id",
                table: "change_board_members",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_board_members_user_id",
                table: "change_board_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_boards_chair_id",
                table: "change_boards",
                column: "chair_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_boards_deputy_chair_id",
                table: "change_boards",
                column: "deputy_chair_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_boards_tenant_id_kind",
                table: "change_boards",
                columns: new[] { "tenant_id", "kind" },
                unique: true);

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
                name: "cab_meeting_members");

            migrationBuilder.DropTable(
                name: "cab_votes");

            migrationBuilder.DropTable(
                name: "change_board_members");

            migrationBuilder.DropTable(
                name: "cab_agenda_items");

            migrationBuilder.DropTable(
                name: "change_boards");

            migrationBuilder.DropTable(
                name: "cab_meetings");
        }
    }
}
