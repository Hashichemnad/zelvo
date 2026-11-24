using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Knowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ticket_id",
                table: "ticket_attachments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "article_id",
                table: "ticket_attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "kb_articles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    working_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    view_count = table.Column<long>(type: "bigint", nullable: false),
                    helpful_count = table.Column<int>(type: "integer", nullable: false),
                    not_helpful_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_articles", x => x.id);
                    table.CheckConstraint("ck_kb_articles_counts", "view_count >= 0 AND helpful_count >= 0 AND not_helpful_count >= 0");
                    table.CheckConstraint("ck_kb_articles_published", "state <> 'Published' OR published_version_id IS NOT NULL");
                    table.CheckConstraint("ck_kb_articles_state", "state IN ('Draft', 'InReview', 'Published', 'Retired')");
                    table.CheckConstraint("ck_kb_articles_visibility", "visibility IN ('Internal', 'External')");
                    table.ForeignKey(
                        name: "fk_kb_articles_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kb_articles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kb_articles_tickets_source_ticket_id",
                        column: x => x.source_ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_kb_articles_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kb_article_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    keywords = table.Column<string[]>(type: "text[]", nullable: false),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    change_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_edited_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    body_ar = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    body_en = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    summary_ar = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    summary_en = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_article_versions", x => x.id);
                    table.CheckConstraint("ck_kb_article_versions_number", "number >= 1");
                    table.CheckConstraint("ck_kb_article_versions_state", "state IN ('Draft', 'InReview', 'Published', 'Superseded', 'Retired')");
                    table.CheckConstraint("ck_kb_article_versions_title", "title_en IS NOT NULL OR title_ar IS NOT NULL");
                    table.CheckConstraint("ck_kb_article_versions_visibility", "visibility IN ('Internal', 'External')");
                    table.ForeignKey(
                        name: "fk_kb_article_versions_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kb_article_versions_kb_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_kb_article_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kb_article_views",
                columns: table => new
                {
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    viewer_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_article_views", x => new { x.article_id, x.viewer_hash, x.day });
                    table.CheckConstraint("ck_kb_article_views_viewer_hash", "viewer_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_kb_article_views_kb_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_kb_article_views_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kb_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visitor_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    helpful = table.Column<bool>(type: "boolean", nullable: false),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_feedback", x => x.id);
                    table.CheckConstraint("ck_kb_feedback_visitor_hash", "visitor_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_kb_feedback_who", "(user_id IS NULL) <> (visitor_hash IS NULL)");
                    table.ForeignKey(
                        name: "fk_kb_feedback_kb_article_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "kb_article_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_kb_feedback_kb_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_kb_feedback_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kb_feedback_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_article_id",
                table: "ticket_attachments",
                column: "article_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_tenant_id_article_id",
                table: "ticket_attachments",
                columns: new[] { "tenant_id", "article_id" },
                filter: "article_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "(ticket_id IS NULL) <> (article_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_kb_article_versions_article_id_number",
                table: "kb_article_versions",
                columns: new[] { "article_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kb_article_versions_category_id",
                table: "kb_article_versions",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_article_versions_tenant_id_state",
                table: "kb_article_versions",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_kb_article_views_tenant_id_day",
                table: "kb_article_views",
                columns: new[] { "tenant_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_category_id",
                table: "kb_articles",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_created_by_id",
                table: "kb_articles",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_source_ticket_id",
                table: "kb_articles",
                column: "source_ticket_id",
                filter: "source_ticket_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_tenant_id_number",
                table: "kb_articles",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_tenant_id_sequence",
                table: "kb_articles",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_tenant_id_state_visibility",
                table: "kb_articles",
                columns: new[] { "tenant_id", "state", "visibility" });

            migrationBuilder.CreateIndex(
                name: "ix_kb_feedback_article_id_user_id",
                table: "kb_feedback",
                columns: new[] { "article_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_kb_feedback_article_id_visitor_hash",
                table: "kb_feedback",
                columns: new[] { "article_id", "visitor_hash" },
                unique: true,
                filter: "visitor_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_kb_feedback_tenant_id_article_id_created_at",
                table: "kb_feedback",
                columns: new[] { "tenant_id", "article_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kb_feedback_user_id",
                table: "kb_feedback",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_feedback_version_id",
                table: "kb_feedback",
                column: "version_id");

            migrationBuilder.AddForeignKey(
                name: "fk_ticket_attachments_kb_articles_article_id",
                table: "ticket_attachments",
                column: "article_id",
                principalTable: "kb_articles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // Knowledge belongs to one tenant (see TenantIsolationMigrationExtensions).
            migrationBuilder.EnableTenantIsolation("kb_articles");
            migrationBuilder.EnableTenantIsolation("kb_article_versions");
            migrationBuilder.EnableTenantIsolation("kb_feedback");
            migrationBuilder.EnableTenantIsolation("kb_article_views");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantIsolation("kb_article_views");
            migrationBuilder.DisableTenantIsolation("kb_feedback");
            migrationBuilder.DisableTenantIsolation("kb_article_versions");
            migrationBuilder.DisableTenantIsolation("kb_articles");

            migrationBuilder.DropForeignKey(
                name: "fk_ticket_attachments_kb_articles_article_id",
                table: "ticket_attachments");

            migrationBuilder.DropTable(
                name: "kb_article_views");

            migrationBuilder.DropTable(
                name: "kb_feedback");

            migrationBuilder.DropTable(
                name: "kb_article_versions");

            migrationBuilder.DropTable(
                name: "kb_articles");

            migrationBuilder.DropIndex(
                name: "ix_ticket_attachments_article_id",
                table: "ticket_attachments");

            migrationBuilder.DropIndex(
                name: "ix_ticket_attachments_tenant_id_article_id",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.DropColumn(
                name: "article_id",
                table: "ticket_attachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "ticket_id",
                table: "ticket_attachments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
