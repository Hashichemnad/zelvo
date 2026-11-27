using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlainLanguageRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ai_call_id",
                table: "workflow_rules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approved_at",
                table: "workflow_rules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by_id",
                table: "workflow_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_draft",
                table: "workflow_rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "source_language",
                table: "workflow_rules",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_text",
                table: "workflow_rules",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unmapped",
                table: "workflow_rules",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_rules_approved_by_id",
                table: "workflow_rules",
                column: "approved_by_id");

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_rules_users_approved_by_id",
                table: "workflow_rules",
                column: "approved_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflow_rules_users_approved_by_id",
                table: "workflow_rules");

            migrationBuilder.DropIndex(
                name: "ix_workflow_rules_approved_by_id",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "ai_call_id",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "approved_at",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "approved_by_id",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "is_draft",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "source_language",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "source_text",
                table: "workflow_rules");

            migrationBuilder.DropColumn(
                name: "unmapped",
                table: "workflow_rules");
        }
    }
}
