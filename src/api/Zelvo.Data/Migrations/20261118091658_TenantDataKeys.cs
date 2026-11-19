using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantDataKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_data_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kek_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rewrapped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_data_keys", x => x.id);
                    table.CheckConstraint("ck_tenant_data_keys_kek_id", "kek_id ~ '^[a-z]+:[A-Za-z0-9._/-]+$'");
                    table.ForeignKey(
                        name: "fk_tenant_data_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_data_keys_active",
                table: "tenant_data_keys",
                column: "tenant_id",
                unique: true,
                filter: "retired_at IS NULL");

            // Data keys belong to one tenant (see TenantIsolationMigrationExtensions).
            migrationBuilder.EnableTenantIsolation("tenant_data_keys");

            // A deleted data key makes every secret written with it unreadable, so keys are never
            // deleted through the application; retiring a key keeps it for decryption.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_tenant_data_keys_no_delete() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'tenant data keys cannot be deleted; retire them instead'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER tenant_data_keys_no_delete BEFORE DELETE ON tenant_data_keys
                    FOR EACH ROW EXECUTE FUNCTION zelvo_tenant_data_keys_no_delete();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tenant_data_keys_no_delete ON tenant_data_keys;
                DROP FUNCTION IF EXISTS zelvo_tenant_data_keys_no_delete();
                """);
            migrationBuilder.DisableTenantIsolation("tenant_data_keys");
            migrationBuilder.DropTable(
                name: "tenant_data_keys");
        }
    }
}
