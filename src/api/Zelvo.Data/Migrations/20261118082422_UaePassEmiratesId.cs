using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class UaePassEmiratesId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "emirates_id",
                table: "users",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_emirates_id",
                table: "users",
                columns: new[] { "tenant_id", "emirates_id" },
                unique: true,
                filter: "emirates_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_emirates_id",
                table: "users",
                sql: "emirates_id ~ '^784[0-9]{12}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id_emirates_id",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_emirates_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "emirates_id",
                table: "users");
        }
    }
}
