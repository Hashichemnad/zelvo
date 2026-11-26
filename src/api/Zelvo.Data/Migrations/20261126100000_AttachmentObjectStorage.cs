using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <summary>
    /// Task F6: attachments in S3-compatible object storage. The worker's sweeper removes objects
    /// whose attachment row is gone, tenant by tenant under each tenant's row-level security; this
    /// function lists the tenants for it (ids and slugs only), like the other jobs' functions.
    /// </summary>
    [DbContext(typeof(ZelvoDbContext))]
    [Migration("20261126100000_AttachmentObjectStorage")]
    public partial class AttachmentObjectStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_attachment_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text FROM public.tenants t
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS zelvo_attachment_tenants();");
        }
    }
}
