using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.AddColumn<Guid>(
                name: "service_id",
                table: "ticket_attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "catalog_service_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_service_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_service_templates_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_service_templates_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "catalog_services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    catalog_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    category_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    editor_catalog_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    working_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    suspended_message_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    suspended_message_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_services", x => x.id);
                    table.CheckConstraint("ck_catalog_services_code", "code ~ '^[A-Z0-9][A-Z0-9-]{0,28}[A-Z0-9]$'");
                    table.CheckConstraint("ck_catalog_services_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_catalog_services_published", "state IN ('Draft', 'UnderReview') OR published_version_id IS NOT NULL");
                    table.CheckConstraint("ck_catalog_services_state", "state IN ('Draft', 'UnderReview', 'Active', 'Suspended', 'Retired')");
                    table.ForeignKey(
                        name: "fk_catalog_services_catalog_services_parent_id",
                        column: x => x.parent_id,
                        principalTable: "catalog_services",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_catalog_services_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_services_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "service_catalogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_service_catalogs", x => x.id);
                    table.CheckConstraint("ck_service_catalogs_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$'");
                    table.ForeignKey(
                        name: "fk_service_catalogs_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_catalogs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_service_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    change_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_edited_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_service_versions", x => x.id);
                    table.CheckConstraint("ck_catalog_service_versions_number", "number >= 1");
                    table.CheckConstraint("ck_catalog_service_versions_state", "state IN ('Draft', 'UnderReview', 'Published', 'Superseded', 'Retired')");
                    table.ForeignKey(
                        name: "fk_catalog_service_versions_catalog_services_service_id",
                        column: x => x.service_id,
                        principalTable: "catalog_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_service_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_service_versions_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_catalog_service_versions_users_last_edited_by_id",
                        column: x => x.last_edited_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_catalog_service_versions_users_reviewed_by_id",
                        column: x => x.reviewed_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_catalog_service_versions_users_submitted_by_id",
                        column: x => x.submitted_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "catalog_editors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_editors", x => x.id);
                    table.CheckConstraint("ck_catalog_editors_one", "num_nonnulls(user_id, group_id) = 1");
                    table.ForeignKey(
                        name: "fk_catalog_editors_service_catalogs_catalog_id",
                        column: x => x.catalog_id,
                        principalTable: "service_catalogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_editors_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_editors_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_editors_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_requests",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_version_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_requests", x => x.ticket_id);
                    table.ForeignKey(
                        name: "fk_service_requests_catalog_service_versions_service_version_id",
                        column: x => x.service_version_id,
                        principalTable: "catalog_service_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_requests_catalog_services_service_id",
                        column: x => x.service_id,
                        principalTable: "catalog_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_requests_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_service_id",
                table: "ticket_attachments",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_tenant_id_service_id",
                table: "ticket_attachments",
                columns: new[] { "tenant_id", "service_id" },
                filter: "service_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "num_nonnulls(ticket_id, article_id, service_id) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND service_id IS NULL AND form_field_key IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_editors_catalog_id_user_id_group_id",
                table: "catalog_editors",
                columns: new[] { "catalog_id", "user_id", "group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_editors_group_id",
                table: "catalog_editors",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_editors_tenant_id",
                table: "catalog_editors",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_editors_user_id",
                table: "catalog_editors",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_templates_created_by_id",
                table: "catalog_service_templates",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_templates_tenant_id",
                table: "catalog_service_templates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_versions_created_by_id",
                table: "catalog_service_versions",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_versions_last_edited_by_id",
                table: "catalog_service_versions",
                column: "last_edited_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_versions_reviewed_by_id",
                table: "catalog_service_versions",
                column: "reviewed_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_versions_service_id_number",
                table: "catalog_service_versions",
                columns: new[] { "service_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_versions_submitted_by_id",
                table: "catalog_service_versions",
                column: "submitted_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_service_versions_tenant_id_state",
                table: "catalog_service_versions",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_services_created_by_id",
                table: "catalog_services",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_services_parent_id",
                table: "catalog_services",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_services_published_version_id",
                table: "catalog_services",
                column: "published_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_services_tenant_id_code",
                table: "catalog_services",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_services_tenant_id_state",
                table: "catalog_services",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_services_working_version_id",
                table: "catalog_services",
                column: "working_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_catalogs_department_id",
                table: "service_catalogs",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_catalogs_tenant_id_key",
                table: "service_catalogs",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_service_id",
                table: "service_requests",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_service_version_id",
                table: "service_requests",
                column: "service_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_tenant_id_service_id",
                table: "service_requests",
                columns: new[] { "tenant_id", "service_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_ticket_attachments_catalog_services_service_id",
                table: "ticket_attachments",
                column: "service_id",
                principalTable: "catalog_services",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            foreach (var table in new[] { "service_catalogs", "catalog_editors", "catalog_services", "catalog_service_versions", "catalog_service_templates", "service_requests" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Task C2: service files have no ticket either; they are not form uploads.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zelvo_tenants_with_stale_form_uploads(before timestamptz)
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.ticket_attachments a
                                  WHERE a.tenant_id = t.id AND a.ticket_id IS NULL AND a.article_id IS NULL AND a.service_id IS NULL AND a.uploaded_at < before)
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zelvo_tenants_with_stale_form_uploads(before timestamptz)
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

            foreach (var table in new[] { "service_catalogs", "catalog_editors", "catalog_services", "catalog_service_versions", "catalog_service_templates", "service_requests" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_ticket_attachments_catalog_services_service_id",
                table: "ticket_attachments");

            migrationBuilder.DropTable(
                name: "catalog_editors");

            migrationBuilder.DropTable(
                name: "catalog_service_templates");

            migrationBuilder.DropTable(
                name: "service_requests");

            migrationBuilder.DropTable(
                name: "service_catalogs");

            migrationBuilder.DropTable(
                name: "catalog_service_versions");

            migrationBuilder.DropTable(
                name: "catalog_services");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropIndex(
                name: "ix_ticket_attachments_service_id",
                table: "ticket_attachments");

            migrationBuilder.DropIndex(
                name: "ix_ticket_attachments_tenant_id_service_id",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.DropColumn(
                name: "service_id",
                table: "ticket_attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "num_nonnulls(ticket_id, article_id) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND form_field_key IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident')");
        }
    }
}
