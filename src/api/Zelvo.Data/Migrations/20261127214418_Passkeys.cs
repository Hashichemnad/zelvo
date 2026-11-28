using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Passkeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "passkeys",
                table: "identity_settings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Task J3: existing tenants get passkeys allowed, the default for new ones.
                defaultValue: "Allowed");

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_settings_passkeys",
                table: "identity_settings",
                sql: "passkeys IN ('Off', 'Allowed', 'RequiredForStaff')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_settings_passkeys",
                table: "identity_settings");

            migrationBuilder.DropColumn(
                name: "passkeys",
                table: "identity_settings");
        }
    }
}
