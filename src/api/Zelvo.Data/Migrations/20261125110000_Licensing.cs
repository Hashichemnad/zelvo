using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <summary>
    /// Task F3 (module 12): the installation's identity and its license files. These tables belong
    /// to the whole installation, not to a tenant, so they have no tenant column or row-level
    /// security; every service reads them with its own connection (<c>Zelvo.Licensing</c>). They
    /// are not in the EF model: only the licensing code reads and writes them.
    /// </summary>
    [DbContext(typeof(ZelvoDbContext))]
    [Migration("20261125110000_Licensing")]
    public partial class Licensing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE installation (
                    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                    id uuid NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    watermark varchar(40)
                );
                -- A random id made once, part of the fingerprint: a copied database keeps it, but
                -- then the database id or the servers differ.
                INSERT INTO installation (id) VALUES (gen_random_uuid());

                CREATE TABLE installation_licenses (
                    id uuid PRIMARY KEY,
                    file_type varchar(10) NOT NULL CHECK (file_type IN ('License', 'Upgrade')),
                    license_id varchar(80) NOT NULL,
                    content bytea NOT NULL CHECK (octet_length(content) <= 1048576),
                    imported_at timestamptz NOT NULL,
                    imported_by_user_id uuid,
                    imported_by_tenant_id uuid,
                    file_name varchar(200),
                    superseded_at timestamptz
                );
                CREATE INDEX ix_installation_licenses_current ON installation_licenses (imported_at) WHERE superseded_at IS NULL;

                -- The database part of the fingerprint: PostgreSQL's system identifier, made when the
                -- database cluster was created. Restoring a backup into another cluster changes it.
                -- pg_control_system() is for superusers, so the application role asks through this.
                CREATE FUNCTION zelvo_database_identifier()
                RETURNS text
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog
                AS $$ SELECT system_identifier::text FROM pg_control_system() $$;
                REVOKE ALL ON FUNCTION zelvo_database_identifier() FROM PUBLIC;
                """);

            // The application role may read the installation and add license files; nobody
            // through it changes the installation id or removes a license file.
            migrationBuilder.Sql("""
                DO $$
                DECLARE r text;
                BEGIN
                    FOR r IN SELECT DISTINCT grantee FROM information_schema.role_table_grants
                             WHERE table_schema = 'public' AND table_name = 'tenants' AND privilege_type = 'SELECT'
                               AND grantee NOT IN ('PUBLIC', current_user)
                    LOOP
                        EXECUTE format('GRANT EXECUTE ON FUNCTION zelvo_database_identifier() TO %I', r);
                        EXECUTE format('REVOKE INSERT, DELETE, TRUNCATE ON installation FROM %I', r);
                        EXECUTE format('REVOKE DELETE, TRUNCATE ON installation_licenses FROM %I', r);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS zelvo_database_identifier();
                DROP TABLE IF EXISTS installation_licenses;
                DROP TABLE IF EXISTS installation;
                """);
        }
    }
}
