using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Assets : Migration
    {
        private static readonly string[] Tables = ["assets", "asset_tag_sequences", "asset_contracts", "asset_contract_items", "software_licenses"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_subject",
                table: "document_exports");

            migrationBuilder.CreateTable(
                name: "asset_contracts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    cost_aed = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expiry_alerted_for = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_contracts", x => x.id);
                    table.CheckConstraint("ck_asset_contracts_cost", "cost_aed IS NULL OR cost_aed >= 0");
                    table.CheckConstraint("ck_asset_contracts_dates", "starts_on IS NULL OR starts_on <= ends_on");
                    table.CheckConstraint("ck_asset_contracts_kind", "kind IN ('Warranty', 'Support', 'Maintenance', 'Lease')");
                    table.ForeignKey(
                        name: "fk_asset_contracts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_asset_contracts_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "asset_tag_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_tag_sequences", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_asset_tag_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assigned_to_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    request_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_date = table.Column<DateOnly>(type: "date", nullable: true),
                    purchase_cost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    order_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.configuration_item_id);
                    table.CheckConstraint("ck_assets_assigned", "(state = 'Assigned') = (assigned_to_id IS NOT NULL)");
                    table.CheckConstraint("ck_assets_cost", "purchase_cost IS NULL OR purchase_cost >= 0");
                    table.CheckConstraint("ck_assets_state", "state IN ('Requested', 'Ordered', 'Received', 'InStock', 'Assigned', 'InRepair', 'Retired', 'Disposed')");
                    table.CheckConstraint("ck_assets_tag", "tag ~ '^[A-Z0-9]([A-Z0-9-]{0,30}[A-Z0-9])?$'");
                    table.ForeignKey(
                        name: "fk_assets_configuration_items_configuration_item_id",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assets_tickets_request_ticket_id",
                        column: x => x.request_ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_assets_users_assigned_to_id",
                        column: x => x.assigned_to_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_licenses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    product_keys = table.Column<string>(type: "jsonb", nullable: false),
                    metric = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cost_aed = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expiry_alerted_for = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_licenses", x => x.id);
                    table.CheckConstraint("ck_software_licenses_cost", "cost_aed IS NULL OR cost_aed >= 0");
                    table.CheckConstraint("ck_software_licenses_dates", "starts_on IS NULL OR ends_on IS NULL OR starts_on <= ends_on");
                    table.CheckConstraint("ck_software_licenses_metric", "metric IN ('Device', 'User')");
                    table.CheckConstraint("ck_software_licenses_products", "jsonb_typeof(product_keys) = 'array'");
                    table.CheckConstraint("ck_software_licenses_quantity", "quantity BETWEEN 0 AND 10000000");
                    table.ForeignKey(
                        name: "fk_software_licenses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_software_licenses_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "asset_contract_items",
                columns: table => new
                {
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_contract_items", x => new { x.contract_id, x.configuration_item_id });
                    table.ForeignKey(
                        name: "fk_asset_contract_items_asset_contracts_contract_id",
                        column: x => x.contract_id,
                        principalTable: "asset_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_contract_items_assets_configuration_item_id",
                        column: x => x.configuration_item_id,
                        principalTable: "assets",
                        principalColumn: "configuration_item_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_contract_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports",
                sql: "kind IN ('Incident', 'ServiceRequest', 'Report', 'AssetLabels')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_subject",
                table: "document_exports",
                sql: "(kind IN ('Report', 'AssetLabels')) = (entity_id IS NULL AND parameters IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_asset_contract_items_configuration_item_id",
                table: "asset_contract_items",
                column: "configuration_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_contract_items_tenant_id_configuration_item_id",
                table: "asset_contract_items",
                columns: new[] { "tenant_id", "configuration_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_contracts_owner_id",
                table: "asset_contracts",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_contracts_tenant_id_ends_on",
                table: "asset_contracts",
                columns: new[] { "tenant_id", "ends_on" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_assigned_to_id",
                table: "assets",
                column: "assigned_to_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_request_ticket_id",
                table: "assets",
                column: "request_ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_assigned_to_id",
                table: "assets",
                columns: new[] { "tenant_id", "assigned_to_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_state",
                table: "assets",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_tag",
                table: "assets",
                columns: new[] { "tenant_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_licenses_owner_id",
                table: "software_licenses",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_software_licenses_tenant_id_ends_on",
                table: "software_licenses",
                columns: new[] { "tenant_id", "ends_on" });

            migrationBuilder.CreateIndex(
                name: "ix_software_licenses_tenant_id_name",
                table: "software_licenses",
                columns: new[] { "tenant_id", "name" });

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The expiry alert job looks only at tenants with contracts or licences.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_asset_job_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active' AND (
                        EXISTS (SELECT 1 FROM public.asset_contracts c WHERE c.tenant_id = t.id)
                        OR EXISTS (SELECT 1 FROM public.software_licenses l WHERE l.tenant_id = t.id AND l.ends_on IS NOT NULL))
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_asset_job_tenants();");
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "asset_contract_items");

            migrationBuilder.DropTable(
                name: "asset_tag_sequences");

            migrationBuilder.DropTable(
                name: "software_licenses");

            migrationBuilder.DropTable(
                name: "asset_contracts");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_exports_subject",
                table: "document_exports");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_kind",
                table: "document_exports",
                sql: "kind IN ('Incident', 'ServiceRequest', 'Report')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_exports_subject",
                table: "document_exports",
                sql: "(kind = 'Report') = (entity_id IS NULL AND parameters IS NOT NULL)");
        }
    }
}
