using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeWhatIf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "what_if",
                table: "changes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "what_if_explained_at",
                table: "changes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "what_if_explanation",
                table: "changes",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "what_if_explanation_language",
                table: "changes",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "what_if",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "what_if_explained_at",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "what_if_explanation",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "what_if_explanation_language",
                table: "changes");
        }
    }
}
