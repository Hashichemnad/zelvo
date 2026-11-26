using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <summary>
    /// Task F6 (module 12 tamper detection): each component's last release integrity check. Like
    /// <c>installation</c>, the table belongs to the whole installation (no tenant column, no
    /// row-level security) and is not in the EF model: <c>Zelvo.Licensing</c> reads and writes it.
    /// The application role may report but not remove a component.
    /// </summary>
    [DbContext(typeof(ZelvoDbContext))]
    [Migration("20261126090000_ReleaseIntegrity")]
    public partial class ReleaseIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE installation_components (
                    component varchar(40) PRIMARY KEY,
                    status varchar(40) NOT NULL,
                    version varchar(40),
                    build_version varchar(40) NOT NULL,
                    detail varchar(500) NOT NULL,
                    checked_at timestamptz NOT NULL
                );

                DO $$
                DECLARE r text;
                BEGIN
                    FOR r IN SELECT DISTINCT grantee FROM information_schema.role_table_grants
                             WHERE table_schema = 'public' AND table_name = 'tenants' AND privilege_type = 'SELECT'
                               AND grantee NOT IN ('PUBLIC', current_user)
                    LOOP
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON installation_components TO %I', r);
                        EXECUTE format('REVOKE DELETE, TRUNCATE ON installation_components FROM %I', r);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS installation_components;");
        }
    }
}
