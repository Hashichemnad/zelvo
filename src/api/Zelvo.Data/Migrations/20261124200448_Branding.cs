using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Branding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.AddColumn<string>(
                name: "branding_slot",
                table: "ticket_attachments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    staff_view = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_exports", x => x.id);
                    table.CheckConstraint("ck_document_exports_kind", "kind IN ('Incident', 'ServiceRequest')");
                    table.CheckConstraint("ck_document_exports_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_document_exports_status", "status IN ('Pending', 'Ready', 'Failed')");
                    table.ForeignKey(
                        name: "fk_document_exports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_exports_tickets_entity_id",
                        column: x => x.entity_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_document_exports_users_requested_by_id",
                        column: x => x.requested_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sender_domains",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    local_part = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dkim_selector = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    dkim_public_key = table.Column<string>(type: "text", nullable: false),
                    dkim_private_key_ciphertext = table.Column<string>(type: "text", nullable: false),
                    spf = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    dkim = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    dmarc = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verified = table.Column<bool>(type: "boolean", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sender_domains", x => x.tenant_id);
                    table.CheckConstraint("ck_sender_domains_dkim", "dkim IN ('Unknown', 'Passed', 'Missing', 'Wrong')");
                    table.CheckConstraint("ck_sender_domains_dmarc", "dmarc IN ('Unknown', 'Passed', 'Missing', 'Wrong')");
                    table.CheckConstraint("ck_sender_domains_domain", "domain ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'");
                    table.CheckConstraint("ck_sender_domains_local_part", "local_part ~ '^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$'");
                    table.CheckConstraint("ck_sender_domains_spf", "spf IN ('Unknown', 'Passed', 'Missing', 'Wrong')");
                    table.ForeignKey(
                        name: "fk_sender_domains_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_brandings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft = table.Column<string>(type: "jsonb", nullable: false),
                    published = table.Column<string>(type: "jsonb", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_brandings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_tenant_brandings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_brandings_users_published_by_id",
                        column: x => x.published_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_domains",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verification_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    certificate_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    certificate_pem = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: true),
                    private_key_ciphertext = table.Column<string>(type: "text", nullable: true),
                    certificate_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_domains", x => x.id);
                    table.CheckConstraint("ck_tenant_domains_certificate_mode", "certificate_mode IN ('Automatic', 'Uploaded')");
                    table.CheckConstraint("ck_tenant_domains_host", "host ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'");
                    table.CheckConstraint("ck_tenant_domains_status", "status IN ('Pending', 'Verified')");
                    table.CheckConstraint("ck_tenant_domains_uploaded", "certificate_mode <> 'Uploaded' OR (certificate_pem IS NOT NULL AND private_key_ciphertext IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_tenant_domains_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_branding_slot",
                table: "ticket_attachments",
                sql: "branding_slot IS NULL OR branding_slot IN ('LogoLight', 'LogoDark', 'Favicon', 'LoginImage')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "num_nonnulls(ticket_id, article_id, service_id, branding_slot) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND service_id IS NULL AND branding_slot IS NULL AND form_field_key IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_document_exports_due",
                table: "document_exports",
                column: "created_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_document_exports_entity_id",
                table: "document_exports",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_exports_requested_by_id",
                table: "document_exports",
                column: "requested_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_exports_tenant_id_status_created_at",
                table: "document_exports",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_brandings_published_by_id",
                table: "tenant_brandings",
                column: "published_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_host",
                table: "tenant_domains",
                column: "host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_tenant_id",
                table: "tenant_domains",
                column: "tenant_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Custom domains (task F1): the web app and the reverse proxy find the tenant of a host
            // before any tenant is set. Verified domains only; the slug and nothing else.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_tenant_for_host(p_host text)
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenant_domains d
                    JOIN public.tenants t ON t.id = d.tenant_id
                    WHERE d.host = lower(p_host) AND d.status = 'Verified' AND t.status = 'Active'
                $$;
                """);

            // Tenants with PDF exports waiting for the worker.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_export_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.document_exports e
                                  WHERE e.tenant_id = t.id AND e.status = 'Pending' AND (e.lease_until IS NULL OR e.lease_until < now()))
                $$;
                """);

            // Exports are kept for a day.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_document_exports(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_count bigint;
                BEGIN
                    DELETE FROM public.document_exports WHERE created_at < p_before;
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END;
                $$;
                """);

            // Branding images have no ticket either; they are not form uploads.
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
                                  WHERE a.tenant_id = t.id AND a.ticket_id IS NULL AND a.article_id IS NULL AND a.service_id IS NULL
                                    AND a.branding_slot IS NULL AND a.uploaded_at < before)
                $$;
                """);
        }

        private static readonly string[] Tables = ["tenant_brandings", "tenant_domains", "sender_domains", "document_exports"];

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
                                  WHERE a.tenant_id = t.id AND a.ticket_id IS NULL AND a.article_id IS NULL AND a.service_id IS NULL AND a.uploaded_at < before)
                $$;
                DROP FUNCTION zelvo_purge_document_exports(timestamptz);
                DROP FUNCTION zelvo_export_tenants();
                DROP FUNCTION zelvo_tenant_for_host(text);
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "document_exports");

            migrationBuilder.DropTable(
                name: "sender_domains");

            migrationBuilder.DropTable(
                name: "tenant_brandings");

            migrationBuilder.DropTable(
                name: "tenant_domains");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_branding_slot",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments");

            migrationBuilder.DropColumn(
                name: "branding_slot",
                table: "ticket_attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_owner",
                table: "ticket_attachments",
                sql: "num_nonnulls(ticket_id, article_id, service_id) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND service_id IS NULL AND form_field_key IS NOT NULL)");
        }
    }
}
