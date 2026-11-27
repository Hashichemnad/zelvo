using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstallableAppIcon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_branding_slot",
                table: "ticket_attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_branding_slot",
                table: "ticket_attachments",
                sql: "branding_slot IS NULL OR branding_slot IN ('LogoLight', 'LogoDark', 'Favicon', 'LoginImage', 'AppIcon')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_branding_slot",
                table: "ticket_attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_branding_slot",
                table: "ticket_attachments",
                sql: "branding_slot IS NULL OR branding_slot IN ('LogoLight', 'LogoDark', 'Favicon', 'LoginImage')");
        }
    }
}
