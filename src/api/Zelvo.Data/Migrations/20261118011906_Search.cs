using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_index_states",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: true),
                    last_indexed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    rebuild_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rebuild_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_index_states", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_search_index_states_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    document_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    document = table.Column<string>(type: "jsonb", nullable: true),
                    purge_before = table.Column<long>(type: "bigint", nullable: true),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_outbox", x => x.id);
                    table.CheckConstraint("ck_search_outbox_document_type", "document_type ~ '^[a-z][a-z0-9-]{0,49}$'");
                    table.CheckConstraint("ck_search_outbox_operation", "operation IN ('Upsert', 'Delete', 'Purge')");
                    table.CheckConstraint("ck_search_outbox_shape", "(operation = 'Purge' AND purge_before IS NOT NULL AND document_type IS NULL AND document_id IS NULL AND document IS NULL) OR (operation = 'Upsert' AND document_type IS NOT NULL AND document_id IS NOT NULL AND document IS NOT NULL) OR (operation = 'Delete' AND document_type IS NOT NULL AND document_id IS NOT NULL AND document IS NULL)");
                    table.ForeignKey(
                        name: "fk_search_outbox_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_outbox_tenant_id_id",
                table: "search_outbox",
                columns: new[] { "tenant_id", "id" },
                filter: "failed_at IS NULL");

            // Both tables belong to one tenant (see TenantIsolationMigrationExtensions).
            migrationBuilder.EnableTenantIsolation("search_outbox");
            migrationBuilder.EnableTenantIsolation("search_index_states");

            // The worker relays the outbox tenant by tenant: this lists tenants with entries to
            // publish (new ones, and ones published longer ago than p_stale, whose message may
            // have been lost) before a tenant is set. It returns ids and slugs only.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_search_outbox_tenants(p_stale interval)
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (
                        SELECT 1 FROM public.search_outbox o
                        WHERE o.tenant_id = t.id AND o.failed_at IS NULL
                          AND (o.published_at IS NULL OR o.published_at < now() - p_stale))
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_search_outbox_tenants(interval);");
            migrationBuilder.DisableTenantIsolation("search_index_states");
            migrationBuilder.DisableTenantIsolation("search_outbox");

            migrationBuilder.DropTable(
                name: "search_index_states");

            migrationBuilder.DropTable(
                name: "search_outbox");
        }
    }
}
