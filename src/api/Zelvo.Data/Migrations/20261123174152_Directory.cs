using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Directory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "available_for_assignment",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "languages",
                table: "users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "routing_rule_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_depth", "depth BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_categories_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_categories_parent", "(depth = 1) = (parent_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "categories",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_categories_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locations", x => x.id);
                    table.CheckConstraint("ck_locations_depth", "depth BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_locations_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_locations_parent", "(depth = 1) = (parent_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_locations_locations_parent_id",
                        column: x => x.parent_id,
                        principalTable: "locations",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_locations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_skills", x => x.id);
                    table.ForeignKey(
                        name: "fk_skills_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_support_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_support_groups_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_skills",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_skills", x => new { x.user_id, x.skill_id });
                    table.ForeignKey(
                        name: "fk_user_skills_skills_skill_id",
                        column: x => x.skill_id,
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_skills_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assignment_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_rules", x => x.id);
                    table.CheckConstraint("ck_assignment_rules_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_assignment_rules_method", "method IN ('GroupQueue', 'RoundRobin')");
                    table.CheckConstraint("ck_assignment_rules_skill", "skill_id IS NULL OR method = 'RoundRobin'");
                    table.ForeignKey(
                        name: "fk_assignment_rules_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignment_rules_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignment_rules_skills_skill_id",
                        column: x => x.skill_id,
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignment_rules_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignment_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_group_members",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_lead = table.Column<bool>(type: "boolean", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_group_members", x => new { x.group_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_support_group_members_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_support_group_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "support_group_rotation",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_group_rotation", x => new { x.group_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_support_group_rotation_support_group_members_group_id_user_",
                        columns: x => new { x.group_id, x.user_id },
                        principalTable: "support_group_members",
                        principalColumns: new[] { "group_id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_languages",
                table: "users",
                sql: "languages <@ ARRAY['en', 'ar']::text[]");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_category_id",
                table: "tickets",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_group_id",
                table: "tickets",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_location_id",
                table: "tickets",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_routing_rule_id",
                table: "tickets",
                column: "routing_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_category_id",
                table: "tickets",
                columns: new[] { "tenant_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_group_id",
                table: "tickets",
                columns: new[] { "tenant_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_location_id",
                table: "tickets",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_rules_category_id",
                table: "assignment_rules",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignment_rules_group_id",
                table: "assignment_rules",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignment_rules_location_id",
                table: "assignment_rules",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignment_rules_skill_id",
                table: "assignment_rules",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignment_rules_tenant_id_position",
                table: "assignment_rules",
                columns: new[] { "tenant_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent_id",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id_parent_id_position",
                table: "categories",
                columns: new[] { "tenant_id", "parent_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_locations_parent_id",
                table: "locations",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_locations_tenant_id_parent_id_position",
                table: "locations",
                columns: new[] { "tenant_id", "parent_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_skills_tenant_id",
                table: "skills",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_group_members_tenant_id_user_id",
                table: "support_group_members",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_support_group_members_user_id",
                table: "support_group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_group_rotation_tenant_id",
                table: "support_group_rotation",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_groups_tenant_id",
                table: "support_groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_skills_skill_id",
                table: "user_skills",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_skills_tenant_id_skill_id",
                table: "user_skills",
                columns: new[] { "tenant_id", "skill_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_assignment_rules_routing_rule_id",
                table: "tickets",
                column: "routing_rule_id",
                principalTable: "assignment_rules",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_categories_category_id",
                table: "tickets",
                column: "category_id",
                principalTable: "categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_locations_location_id",
                table: "tickets",
                column: "location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_support_groups_group_id",
                table: "tickets",
                column: "group_id",
                principalTable: "support_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            foreach (var table in new[] { "categories", "locations", "skills", "user_skills", "support_groups", "support_group_members", "support_group_rotation", "assignment_rules" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "categories", "locations", "skills", "user_skills", "support_groups", "support_group_members", "support_group_rotation", "assignment_rules" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_assignment_rules_routing_rule_id",
                table: "tickets");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_categories_category_id",
                table: "tickets");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_locations_location_id",
                table: "tickets");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_support_groups_group_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "assignment_rules");

            migrationBuilder.DropTable(
                name: "support_group_rotation");

            migrationBuilder.DropTable(
                name: "user_skills");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "locations");

            migrationBuilder.DropTable(
                name: "support_group_members");

            migrationBuilder.DropTable(
                name: "skills");

            migrationBuilder.DropTable(
                name: "support_groups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_languages",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_tickets_category_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_group_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_location_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_routing_rule_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_category_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_group_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_location_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "available_for_assignment",
                table: "users");

            migrationBuilder.DropColumn(
                name: "languages",
                table: "users");

            migrationBuilder.DropColumn(
                name: "category_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "routing_rule_id",
                table: "tickets");
        }
    }
}
