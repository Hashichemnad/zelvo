using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class CmdbDiscovery : Migration
    {
        private static readonly string[] Tables = ["ci_discovery_sources", "ci_source_records", "ci_installed_software", "ci_review_items"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "attribute_sources",
                table: "configuration_items",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_id",
                table: "configuration_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "missing_since",
                table: "configuration_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "identification_rules",
                table: "ci_classes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ci_discovery_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    credential_ciphertext = table.Column<string>(type: "text", nullable: true),
                    credential_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    attribute_priorities = table.Column<string>(type: "jsonb", nullable: false),
                    sync_minutes = table.Column<int>(type: "integer", nullable: false),
                    next_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_sync_stats = table.Column<string>(type: "jsonb", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_discovery_sources", x => x.id);
                    table.CheckConstraint("ck_ci_discovery_sources_kind", "kind IN ('DiscoveryApi', 'Intune', 'EntraDevices', 'Azure', 'ActiveDirectory', 'VCenter')");
                    table.CheckConstraint("ck_ci_discovery_sources_priorities", "jsonb_typeof(attribute_priorities) = 'object'");
                    table.CheckConstraint("ck_ci_discovery_sources_priority", "priority BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_ci_discovery_sources_settings", "jsonb_typeof(settings) = 'object'");
                    table.CheckConstraint("ck_ci_discovery_sources_sync_minutes", "sync_minutes BETWEEN 15 AND 10080");
                    table.ForeignKey(
                        name: "fk_ci_discovery_sources_connectors_connector_id",
                        column: x => x.connector_id,
                        principalTable: "connectors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_discovery_sources_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ci_installed_software",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_installed_software", x => x.id);
                    table.ForeignKey(
                        name: "fk_ci_installed_software_ci_discovery_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "ci_discovery_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_installed_software_configuration_items_configuration_ite",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_installed_software_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ci_source_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    primary_user = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    missing_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_source_records", x => x.id);
                    table.CheckConstraint("ck_ci_source_records_attributes", "jsonb_typeof(attributes) = 'object'");
                    table.CheckConstraint("ck_ci_source_records_linked", "(state = 'Linked') = (configuration_item_id IS NOT NULL)");
                    table.CheckConstraint("ck_ci_source_records_state", "state IN ('Linked', 'Review')");
                    table.ForeignKey(
                        name: "fk_ci_source_records_ci_classes_class_id",
                        column: x => x.class_id,
                        principalTable: "ci_classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_source_records_ci_discovery_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "ci_discovery_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_source_records_configuration_items_configuration_item_id",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_source_records_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ci_review_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    other_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidates = table.Column<string>(type: "jsonb", nullable: false),
                    matched_on = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    matched_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_review_items", x => x.id);
                    table.CheckConstraint("ck_ci_review_items_candidates", "jsonb_typeof(candidates) = 'array'");
                    table.CheckConstraint("ck_ci_review_items_ends", "(kind = 'PossibleDuplicate' AND configuration_item_id IS NOT NULL AND other_item_id IS NOT NULL) OR (kind = 'Ambiguous' AND source_record_id IS NOT NULL)");
                    table.CheckConstraint("ck_ci_review_items_kind", "kind IN ('PossibleDuplicate', 'Ambiguous')");
                    table.CheckConstraint("ck_ci_review_items_state", "state IN ('Open', 'Merged', 'KeptApart', 'Linked', 'Created', 'Closed')");
                    table.ForeignKey(
                        name: "fk_ci_review_items_ci_source_records_source_record_id",
                        column: x => x.source_record_id,
                        principalTable: "ci_source_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_review_items_configuration_items_configuration_item_id",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_review_items_configuration_items_other_item_id",
                        column: x => x.other_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_review_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_review_items_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_merged_into_id",
                table: "configuration_items",
                column: "merged_into_id");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_tenant_id_missing_since",
                table: "configuration_items",
                columns: new[] { "tenant_id", "missing_since" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_discovery_sources_connector_id",
                table: "ci_discovery_sources",
                column: "connector_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_discovery_sources_tenant_id_name",
                table: "ci_discovery_sources",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_discovery_sources_tenant_id_next_sync_at",
                table: "ci_discovery_sources",
                columns: new[] { "tenant_id", "next_sync_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_installed_software_configuration_item_id_source_id_produ",
                table: "ci_installed_software",
                columns: new[] { "configuration_item_id", "source_id", "product_key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_installed_software_source_id",
                table: "ci_installed_software",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_installed_software_tenant_id_product_key",
                table: "ci_installed_software",
                columns: new[] { "tenant_id", "product_key" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_review_items_configuration_item_id_other_item_id",
                table: "ci_review_items",
                columns: new[] { "configuration_item_id", "other_item_id" },
                unique: true,
                filter: "kind = 'PossibleDuplicate'");

            migrationBuilder.CreateIndex(
                name: "ix_ci_review_items_decided_by_id",
                table: "ci_review_items",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_review_items_other_item_id",
                table: "ci_review_items",
                column: "other_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_review_items_source_record_id",
                table: "ci_review_items",
                column: "source_record_id",
                unique: true,
                filter: "kind = 'Ambiguous' AND state = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_ci_review_items_tenant_id_state_created_at",
                table: "ci_review_items",
                columns: new[] { "tenant_id", "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_source_records_class_id",
                table: "ci_source_records",
                column: "class_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_source_records_configuration_item_id",
                table: "ci_source_records",
                column: "configuration_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_source_records_source_id_external_id",
                table: "ci_source_records",
                columns: new[] { "source_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_source_records_tenant_id_configuration_item_id",
                table: "ci_source_records",
                columns: new[] { "tenant_id", "configuration_item_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_configuration_items_configuration_items_merged_into_id",
                table: "configuration_items",
                column: "merged_into_id",
                principalTable: "configuration_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Identifying values of every item (task G2): "key=value" in lower case for each text,
            // number or checkbox value, kept by the database whoever writes the item, so
            // identification rules and duplicate detection find matches through one GIN index.
            migrationBuilder.Sql("""
                ALTER TABLE configuration_items ADD COLUMN identifiers text[] NOT NULL DEFAULT '{}';

                CREATE FUNCTION zelvo_ci_identifiers() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    NEW.identifiers := ARRAY(
                        SELECT DISTINCT lower(e.key || '=' || btrim(e.value))
                        FROM jsonb_each_text(NEW.attributes) AS e
                        WHERE jsonb_typeof(NEW.attributes -> e.key) IN ('string', 'number', 'boolean')
                          AND length(btrim(e.value)) BETWEEN 1 AND 300);
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_configuration_items_identifiers
                    BEFORE INSERT OR UPDATE OF attributes ON configuration_items
                    FOR EACH ROW EXECUTE FUNCTION zelvo_ci_identifiers();

                UPDATE configuration_items SET attributes = attributes;

                CREATE INDEX ix_configuration_items_identifiers ON configuration_items USING gin (identifiers);
                """);

            // The discovery job looks only at tenants with an active pull source that is due.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_cmdb_discovery_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active' AND EXISTS (
                        SELECT 1 FROM public.ci_discovery_sources s
                        WHERE s.tenant_id = t.id AND s.active AND s.kind <> 'DiscoveryApi'
                          AND s.next_sync_at <= now() AND (s.lease_until IS NULL OR s.lease_until < now()))
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_cmdb_discovery_tenants();
                DROP INDEX ix_configuration_items_identifiers;
                DROP TRIGGER trg_configuration_items_identifiers ON configuration_items;
                DROP FUNCTION zelvo_ci_identifiers();
                ALTER TABLE configuration_items DROP COLUMN identifiers;
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_configuration_items_configuration_items_merged_into_id",
                table: "configuration_items");

            migrationBuilder.DropTable(
                name: "ci_installed_software");

            migrationBuilder.DropTable(
                name: "ci_review_items");

            migrationBuilder.DropTable(
                name: "ci_source_records");

            migrationBuilder.DropTable(
                name: "ci_discovery_sources");

            migrationBuilder.DropIndex(
                name: "ix_configuration_items_merged_into_id",
                table: "configuration_items");

            migrationBuilder.DropIndex(
                name: "ix_configuration_items_tenant_id_missing_since",
                table: "configuration_items");

            migrationBuilder.DropColumn(
                name: "attribute_sources",
                table: "configuration_items");

            migrationBuilder.DropColumn(
                name: "merged_into_id",
                table: "configuration_items");

            migrationBuilder.DropColumn(
                name: "missing_since",
                table: "configuration_items");

            migrationBuilder.DropColumn(
                name: "identification_rules",
                table: "ci_classes");
        }
    }
}
