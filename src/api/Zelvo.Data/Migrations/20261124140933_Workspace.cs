using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Workspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canned_responses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    body_ar = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    body_en = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_canned_responses", x => x.id);
                    table.CheckConstraint("ck_canned_responses_owner", "(scope = 'Tenant' AND group_id IS NULL AND owner_id IS NULL) OR (scope = 'Group' AND group_id IS NOT NULL AND owner_id IS NULL) OR (scope = 'Personal' AND group_id IS NULL AND owner_id IS NOT NULL)");
                    table.CheckConstraint("ck_canned_responses_scope", "scope IN ('Personal', 'Group', 'Tenant')");
                    table.ForeignKey(
                        name: "fk_canned_responses_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_canned_responses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_canned_responses_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_canned_responses_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_views", x => x.id);
                    table.CheckConstraint("ck_saved_views_group", "(scope = 'Group') = (group_id IS NOT NULL)");
                    table.CheckConstraint("ck_saved_views_scope", "scope IN ('Personal', 'Group', 'Tenant')");
                    table.ForeignKey(
                        name: "fk_saved_views_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_saved_views_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_saved_views_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    by_staff = table.Column<bool>(type: "boolean", nullable: false),
                    canned_response_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_comments", x => x.id);
                    table.CheckConstraint("ck_ticket_comments_internal_by_staff", "visibility = 'Public' OR by_staff");
                    table.CheckConstraint("ck_ticket_comments_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_ticket_comments_visibility", "visibility IN ('Public', 'Internal')");
                    table.ForeignKey(
                        name: "fk_ticket_comments_canned_responses_canned_response_id",
                        column: x => x.canned_response_id,
                        principalTable: "canned_responses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ticket_comments_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_comments_users_author_id",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_comment_mentions",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_comment_mentions", x => new { x.comment_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_ticket_comment_mentions_ticket_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_comment_mentions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_comment_mentions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_created_by_id",
                table: "canned_responses",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_group_id",
                table: "canned_responses",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_owner_id",
                table: "canned_responses",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_tenant_id_scope",
                table: "canned_responses",
                columns: new[] { "tenant_id", "scope" });

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_group_id",
                table: "saved_views",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_owner_id",
                table: "saved_views",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_tenant_id_scope",
                table: "saved_views",
                columns: new[] { "tenant_id", "scope" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comment_mentions_tenant_id_user_id",
                table: "ticket_comment_mentions",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comment_mentions_ticket_id",
                table: "ticket_comment_mentions",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comment_mentions_user_id",
                table: "ticket_comment_mentions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_author_id",
                table: "ticket_comments",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_canned_response_id",
                table: "ticket_comments",
                column: "canned_response_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_tenant_id_ticket_id_created_at",
                table: "ticket_comments",
                columns: new[] { "tenant_id", "ticket_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_ticket_id",
                table: "ticket_comments",
                column: "ticket_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Comments are a faithful record: never changed once written (task B4). The one change
            // allowed is the link to a canned response that was removed (ON DELETE SET NULL).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_ticket_comments_immutable() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF NEW.canned_response_id IS NULL
                        AND ROW(NEW.id, NEW.tenant_id, NEW.ticket_id, NEW.visibility, NEW.body, NEW.language, NEW.author_id, NEW.by_staff, NEW.created_at)
                            IS NOT DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.ticket_id, OLD.visibility, OLD.body, OLD.language, OLD.author_id, OLD.by_staff, OLD.created_at) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'ticket comments cannot be changed' USING ERRCODE = 'integrity_constraint_violation';
                END;
                $$;
                CREATE TRIGGER zelvo_ticket_comments_immutable BEFORE UPDATE ON ticket_comments
                    FOR EACH ROW EXECUTE FUNCTION zelvo_ticket_comments_immutable();
                """);
        }

        private static readonly string[] Tables = ["ticket_comments", "ticket_comment_mentions", "canned_responses", "saved_views"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER zelvo_ticket_comments_immutable ON ticket_comments;
                DROP FUNCTION zelvo_ticket_comments_immutable();
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "saved_views");

            migrationBuilder.DropTable(
                name: "ticket_comment_mentions");

            migrationBuilder.DropTable(
                name: "ticket_comments");

            migrationBuilder.DropTable(
                name: "canned_responses");
        }
    }
}
