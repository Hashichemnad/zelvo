using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Cmdb : Migration
    {
        /// <summary>Tenant-owned tables of the CMDB (module 18, task G1), all with row-level security.</summary>
        private static readonly string[] Tables =
        [
            "ci_classes", "ci_number_sequences", "ci_relationship_types", "configuration_items", "ci_relationships", "ticket_configuration_items",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ci_classes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    family = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    requester_visible = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_ci_classes", x => x.id);
                    table.CheckConstraint("ck_ci_classes_attributes", "jsonb_typeof(attributes) = 'array'");
                    table.CheckConstraint("ck_ci_classes_depth", "depth BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_ci_classes_family", "family IN ('Hardware', 'Software', 'BusinessService', 'Application', 'Network', 'CloudResource')");
                    table.CheckConstraint("ck_ci_classes_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$'");
                    table.CheckConstraint("ck_ci_classes_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_ci_classes_parent", "(depth = 1) = (parent_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_ci_classes_ci_classes_parent_id",
                        column: x => x.parent_id,
                        principalTable: "ci_classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_classes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ci_number_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_number_sequences", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_ci_number_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ci_relationship_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    impact = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    built_in = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    incoming_name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    incoming_name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outgoing_name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outgoing_name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_relationship_types", x => x.id);
                    table.CheckConstraint("ck_ci_relationship_types_impact", "impact IN ('ToSource', 'ToTarget', 'Both', 'None')");
                    table.CheckConstraint("ck_ci_relationship_types_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$'");
                    table.CheckConstraint("ck_ci_relationship_types_target_kind", "target_kind IN ('ConfigurationItem', 'Consumer')");
                    table.ForeignKey(
                        name: "fk_ci_relationship_types_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "configuration_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    owner_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_items", x => x.id);
                    table.CheckConstraint("ck_configuration_items_attributes", "jsonb_typeof(attributes) = 'object'");
                    table.CheckConstraint("ck_configuration_items_name", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_configuration_items_sequence", "sequence >= 1");
                    table.CheckConstraint("ck_configuration_items_status", "status IN ('Planned', 'Active', 'Maintenance', 'Retired')");
                    table.ForeignKey(
                        name: "fk_configuration_items_ci_classes_class_id",
                        column: x => x.class_id,
                        principalTable: "ci_classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_configuration_items_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_configuration_items_support_groups_owner_group_id",
                        column: x => x.owner_group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_configuration_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ci_relationships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_ci_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_customer_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_relationships", x => x.id);
                    table.CheckConstraint("ck_ci_relationships_not_self", "target_ci_id IS NULL OR target_ci_id <> source_id");
                    table.CheckConstraint("ck_ci_relationships_one_target", "num_nonnulls(target_ci_id, target_user_id, target_department_id, target_customer_group_id) = 1");
                    table.ForeignKey(
                        name: "fk_ci_relationships_ci_relationship_types_type_id",
                        column: x => x.type_id,
                        principalTable: "ci_relationship_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_relationships_configuration_items_source_id",
                        column: x => x.source_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_relationships_configuration_items_target_ci_id",
                        column: x => x.target_ci_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_relationships_customer_groups_target_customer_group_id",
                        column: x => x.target_customer_group_id,
                        principalTable: "customer_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_relationships_departments_target_department_id",
                        column: x => x.target_department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_relationships_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_relationships_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ci_relationships_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_configuration_items",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_configuration_items", x => new { x.ticket_id, x.configuration_item_id });
                    table.ForeignKey(
                        name: "fk_ticket_configuration_items_configuration_items_configuratio",
                        column: x => x.configuration_item_id,
                        principalTable: "configuration_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_configuration_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_configuration_items_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_configuration_items_users_added_by_id",
                        column: x => x.added_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ci_classes_parent_id",
                table: "ci_classes",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_classes_tenant_id_key",
                table: "ci_classes",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_classes_tenant_id_parent_id",
                table: "ci_classes",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationship_types_tenant_id_key",
                table: "ci_relationship_types",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_created_by_id",
                table: "ci_relationships",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_target_ci_id",
                table: "ci_relationships",
                column: "target_ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_target_customer_group_id",
                table: "ci_relationships",
                column: "target_customer_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_target_department_id",
                table: "ci_relationships",
                column: "target_department_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_target_user_id",
                table: "ci_relationships",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_tenant_id_target_ci_id",
                table: "ci_relationships",
                columns: new[] { "tenant_id", "target_ci_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_tenant_id_type_id",
                table: "ci_relationships",
                columns: new[] { "tenant_id", "type_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_type_id",
                table: "ci_relationships",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "ux_ci_relationships_ends",
                table: "ci_relationships",
                columns: new[] { "source_id", "type_id", "target_ci_id", "target_user_id", "target_department_id", "target_customer_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_class_id",
                table: "configuration_items",
                column: "class_id");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_location_id",
                table: "configuration_items",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_owner_group_id",
                table: "configuration_items",
                column: "owner_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_tenant_id_class_id_status",
                table: "configuration_items",
                columns: new[] { "tenant_id", "class_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_tenant_id_name",
                table: "configuration_items",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_tenant_id_number",
                table: "configuration_items",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_tenant_id_owner_group_id",
                table: "configuration_items",
                columns: new[] { "tenant_id", "owner_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_items_tenant_id_sequence",
                table: "configuration_items",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_configuration_items_added_by_id",
                table: "ticket_configuration_items",
                column: "added_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_configuration_items_configuration_item_id",
                table: "ticket_configuration_items",
                column: "configuration_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_configuration_items_tenant_id_configuration_item_id",
                table: "ticket_configuration_items",
                columns: new[] { "tenant_id", "configuration_item_id" });

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
                name: "ci_number_sequences");

            migrationBuilder.DropTable(
                name: "ci_relationships");

            migrationBuilder.DropTable(
                name: "ticket_configuration_items");

            migrationBuilder.DropTable(
                name: "ci_relationship_types");

            migrationBuilder.DropTable(
                name: "configuration_items");

            migrationBuilder.DropTable(
                name: "ci_classes");
        }
    }
}
