using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppBuilder : Migration
    {
        /// <summary>Tenant-owned tables of the low-code app builder (module 20, task K3), with row-level security.</summary>
        private static readonly string[] Tables = ["apps", "data_lists", "data_list_items", "app_imports"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "app_id",
                table: "roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "app_id",
                table: "record_types",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    icon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    suspended = table.Column<string>(type: "jsonb", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_apps", x => x.id);
                    table.CheckConstraint("ck_apps_definition", "jsonb_typeof(definition) = 'object'");
                    table.CheckConstraint("ck_apps_key", "key ~ '^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$'");
                    table.CheckConstraint("ck_apps_suspended", "jsonb_typeof(suspended) = 'object'");
                    table.ForeignKey(
                        name: "fk_apps_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    package = table.Column<string>(type: "jsonb", nullable: false),
                    app_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    imported_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    choices = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    app_name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    app_name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_imports", x => x.id);
                    table.CheckConstraint("ck_app_imports_package", "jsonb_typeof(package) = 'object'");
                    table.CheckConstraint("ck_app_imports_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_app_imports_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_app_imports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    columns = table.Column<string>(type: "jsonb", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_lists", x => x.id);
                    table.CheckConstraint("ck_data_lists_columns", "jsonb_typeof(columns) = 'array'");
                    table.CheckConstraint("ck_data_lists_key", "key ~ '^[a-z][a-zA-Z0-9-]{0,39}$'");
                    table.ForeignKey(
                        name: "fk_data_lists_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_data_lists_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_list_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    label_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    label_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_list_items", x => x.id);
                    table.CheckConstraint("ck_data_list_items_values", "jsonb_typeof(values) = 'object'");
                    table.ForeignKey(
                        name: "fk_data_list_items_data_lists_data_list_id",
                        column: x => x.data_list_id,
                        principalTable: "data_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_data_list_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_roles_app_id",
                table: "roles",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_record_types_app_id",
                table: "record_types",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_imports_app_id",
                table: "app_imports",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_imports_tenant_id_status_imported_at",
                table: "app_imports",
                columns: new[] { "tenant_id", "status", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_apps_tenant_id_key",
                table: "apps",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_list_items_data_list_id_key",
                table: "data_list_items",
                columns: new[] { "data_list_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_list_items_tenant_id_data_list_id_position",
                table: "data_list_items",
                columns: new[] { "tenant_id", "data_list_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_data_lists_app_id",
                table: "data_lists",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_lists_tenant_id_key",
                table: "data_lists",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_record_types_apps_app_id",
                table: "record_types",
                column: "app_id",
                principalTable: "apps",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_roles_apps_app_id",
                table: "roles",
                column: "app_id",
                principalTable: "apps",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

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

            migrationBuilder.DropForeignKey(
                name: "fk_record_types_apps_app_id",
                table: "record_types");

            migrationBuilder.DropForeignKey(
                name: "fk_roles_apps_app_id",
                table: "roles");

            migrationBuilder.DropTable(
                name: "app_imports");

            migrationBuilder.DropTable(
                name: "data_list_items");

            migrationBuilder.DropTable(
                name: "data_lists");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropIndex(
                name: "ix_roles_app_id",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "ix_record_types_app_id",
                table: "record_types");

            migrationBuilder.DropColumn(
                name: "app_id",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "app_id",
                table: "record_types");
        }
    }
}
