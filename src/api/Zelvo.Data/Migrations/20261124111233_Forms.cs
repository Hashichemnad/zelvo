using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Forms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "employee_id",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "manager_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "form_field_key",
                table: "ticket_attachments",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.ForeignKey(
                        name: "fk_departments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_set_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: false),
                    published_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_set_versions", x => x.id);
                    table.CheckConstraint("ck_field_set_versions_number", "number >= 1");
                    table.ForeignKey(
                        name: "fk_field_set_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_field_set_versions_users_published_by_id",
                        column: x => x.published_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "field_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    draft_schema = table.Column<string>(type: "jsonb", nullable: false),
                    has_unpublished_changes = table.Column<bool>(type: "boolean", nullable: false),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_field_sets", x => x.id);
                    table.CheckConstraint("ck_field_sets_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$'");
                    table.CheckConstraint("ck_field_sets_published", "state = 'Draft' OR published_version_id IS NOT NULL");
                    table.CheckConstraint("ck_field_sets_state", "state IN ('Draft', 'Published', 'Retired')");
                    table.ForeignKey(
                        name: "fk_field_sets_field_set_versions_published_version_id",
                        column: x => x.published_version_id,
                        principalTable: "field_set_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_field_sets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_field_sets_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "form_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_bindings", x => x.id);
                    table.CheckConstraint("ck_form_bindings_record_type", "record_type IN ('Incident')");
                    table.ForeignKey(
                        name: "fk_form_bindings_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_bindings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "form_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: false),
                    published_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_versions", x => x.id);
                    table.CheckConstraint("ck_form_versions_number", "number >= 1");
                    table.ForeignKey(
                        name: "fk_form_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_form_versions_users_published_by_id",
                        column: x => x.published_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "forms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    draft_schema = table.Column<string>(type: "jsonb", nullable: false),
                    has_unpublished_changes = table.Column<bool>(type: "boolean", nullable: false),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_forms", x => x.id);
                    table.CheckConstraint("ck_forms_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$'");
                    table.CheckConstraint("ck_forms_published", "state = 'Draft' OR published_version_id IS NOT NULL");
                    table.CheckConstraint("ck_forms_state", "state IN ('Draft', 'Published', 'Retired')");
                    table.ForeignKey(
                        name: "fk_forms_form_versions_published_version_id",
                        column: x => x.published_version_id,
                        principalTable: "form_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_forms_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_forms_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ticket_form_responses",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    personal_answers = table.Column<string>(type: "jsonb", nullable: false),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_form_responses", x => x.ticket_id);
                    table.ForeignKey(
                        name: "fk_ticket_form_responses_form_versions_form_version_id",
                        column: x => x.form_version_id,
                        principalTable: "form_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_form_responses_forms_form_id",
                        column: x => x.form_id,
                        principalTable: "forms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_form_responses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_form_responses_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_form_responses_users_submitted_by_id",
                        column: x => x.submitted_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_department_id",
                table: "users",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_location_id",
                table: "users",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_manager_id",
                table: "users",
                column: "manager_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_not_own_manager",
                table: "users",
                sql: "manager_id IS NULL OR manager_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_phone",
                table: "users",
                sql: "phone ~ '^\\+[1-9][0-9]{6,14}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_form_field_key",
                table: "ticket_attachments",
                sql: "form_field_key ~ '^[a-z][a-zA-Z0-9]{0,59}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "num_nonnulls(ticket_id, article_id) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND form_field_key IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_departments_tenant_id",
                table: "departments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_set_versions_field_set_id_number",
                table: "field_set_versions",
                columns: new[] { "field_set_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_set_versions_published_by_id",
                table: "field_set_versions",
                column: "published_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_set_versions_tenant_id",
                table: "field_set_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_sets_created_by_id",
                table: "field_sets",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_sets_published_version_id",
                table: "field_sets",
                column: "published_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_sets_tenant_id_key",
                table: "field_sets",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_form_bindings_category_id",
                table: "form_bindings",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_bindings_form_id",
                table: "form_bindings",
                column: "form_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_bindings_tenant_id_record_type_category_id",
                table: "form_bindings",
                columns: new[] { "tenant_id", "record_type", "category_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_form_versions_form_id_number",
                table: "form_versions",
                columns: new[] { "form_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_form_versions_published_by_id",
                table: "form_versions",
                column: "published_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_versions_tenant_id",
                table: "form_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_forms_created_by_id",
                table: "forms",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_forms_published_version_id",
                table: "forms",
                column: "published_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_forms_tenant_id_key",
                table: "forms",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_form_responses_form_id",
                table: "ticket_form_responses",
                column: "form_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_form_responses_form_version_id",
                table: "ticket_form_responses",
                column: "form_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_form_responses_submitted_by_id",
                table: "ticket_form_responses",
                column: "submitted_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_form_responses_tenant_id",
                table: "ticket_form_responses",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_users_departments_department_id",
                table: "users",
                column: "department_id",
                principalTable: "departments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_users_locations_location_id",
                table: "users",
                column: "location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_users_users_manager_id",
                table: "users",
                column: "manager_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_field_set_versions_field_sets_field_set_id",
                table: "field_set_versions",
                column: "field_set_id",
                principalTable: "field_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_form_bindings_forms_form_id",
                table: "form_bindings",
                column: "form_id",
                principalTable: "forms",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_form_versions_forms_form_id",
                table: "form_versions",
                column: "form_id",
                principalTable: "forms",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            foreach (var table in new[] { "departments", "forms", "form_versions", "field_sets", "field_set_versions", "form_bindings", "ticket_form_responses" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The form upload clean-up job finds tenants with files that were never sent with a form;
            // only ids and slugs come back.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_tenants_with_stale_form_uploads(before timestamptz)
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.ticket_attachments a
                                  WHERE a.tenant_id = t.id AND a.ticket_id IS NULL AND a.article_id IS NULL AND a.uploaded_at < before)
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_tenants_with_stale_form_uploads(timestamptz);");
            foreach (var table in new[] { "departments", "forms", "form_versions", "field_sets", "field_set_versions", "form_bindings", "ticket_form_responses" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_users_departments_department_id",
                table: "users");

            migrationBuilder.DropForeignKey(
                name: "fk_users_locations_location_id",
                table: "users");

            migrationBuilder.DropForeignKey(
                name: "fk_users_users_manager_id",
                table: "users");

            migrationBuilder.DropForeignKey(
                name: "fk_field_set_versions_field_sets_field_set_id",
                table: "field_set_versions");

            migrationBuilder.DropForeignKey(
                name: "fk_form_versions_forms_form_id",
                table: "form_versions");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "form_bindings");

            migrationBuilder.DropTable(
                name: "ticket_form_responses");

            migrationBuilder.DropTable(
                name: "field_sets");

            migrationBuilder.DropTable(
                name: "field_set_versions");

            migrationBuilder.DropTable(
                name: "forms");

            migrationBuilder.DropTable(
                name: "form_versions");

            migrationBuilder.DropIndex(
                name: "ix_users_department_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_location_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_manager_id",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_not_own_manager",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_phone",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_form_field_key",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.DropColumn(
                name: "department_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "employee_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "manager_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "users");

            migrationBuilder.DropColumn(
                name: "form_field_key",
                table: "ticket_attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "(ticket_id IS NULL) <> (article_id IS NULL)");
        }
    }
}
