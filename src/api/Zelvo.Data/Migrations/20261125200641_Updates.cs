using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Updates : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Task F4 (module 13): update packages and their installs belong to the whole installation, so
        /// they have no tenant column or row-level security (like <c>installation</c> of task F3).
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "update_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    package_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    storage_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_tenant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_update_packages", x => x.id);
                    table.CheckConstraint("ck_update_packages_status", "status IN ('Imported', 'Installed', 'Removed')");
                    table.CheckConstraint("ck_update_packages_type", "package_type IN ('Security', 'Feature')");
                });

            migrationBuilder.CreateTable(
                name: "update_installs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    to_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notice_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expected_minutes = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    backup_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_update_installs", x => x.id);
                    table.CheckConstraint("ck_update_installs_minutes", "expected_minutes BETWEEN 1 AND 1440");
                    table.CheckConstraint("ck_update_installs_notice", "notice_from <= scheduled_for");
                    table.CheckConstraint("ck_update_installs_state", "state IN ('Scheduled', 'Running', 'Succeeded', 'RolledBack', 'Failed', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_update_installs_update_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "update_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_update_installs_created_at",
                table: "update_installs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_update_installs_package_id",
                table: "update_installs",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_update_packages_imported_at",
                table: "update_packages",
                column: "imported_at");

            // One install at a time: at most one scheduled or running, whatever its state.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_update_installs_active ON update_installs ((true)) WHERE state IN ('Scheduled', 'Running');");

            // The updater service says it is there (Settings → Updates shows whether it is online).
            migrationBuilder.Sql("ALTER TABLE installation ADD COLUMN updater_seen_at timestamptz;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE installation DROP COLUMN IF EXISTS updater_seen_at;");
            migrationBuilder.DropTable(
                name: "update_installs");

            migrationBuilder.DropTable(
                name: "update_packages");
        }
    }
}
