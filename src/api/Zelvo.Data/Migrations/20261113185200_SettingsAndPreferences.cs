using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettingsAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "calendar",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "digits",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preferred_language",
                table: "users",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_settings", x => new { x.tenant_id, x.key });
                    table.CheckConstraint("ck_tenant_settings_key", "key ~ '^[a-z][a-zA-Z0-9]*(\\.[a-z][a-zA-Z0-9]*)+$'");
                    table.ForeignKey(
                        name: "fk_tenant_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_calendar",
                table: "users",
                sql: "calendar IN ('Gregorian', 'Hijri')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_digits",
                table: "users",
                sql: "digits IN ('Western', 'ArabicIndic')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_preferred_language",
                table: "users",
                sql: "preferred_language IN ('en', 'ar')");

            // Tenant settings belong to one tenant (see TenantIsolationMigrationExtensions).
            migrationBuilder.EnableTenantIsolation("tenant_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantIsolation("tenant_settings");

            migrationBuilder.DropTable(
                name: "tenant_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_calendar",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_digits",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_preferred_language",
                table: "users");

            migrationBuilder.DropColumn(
                name: "calendar",
                table: "users");

            migrationBuilder.DropColumn(
                name: "digits",
                table: "users");

            migrationBuilder.DropColumn(
                name: "preferred_language",
                table: "users");
        }
    }
}
