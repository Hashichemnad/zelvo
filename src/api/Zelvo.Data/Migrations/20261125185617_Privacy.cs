using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Privacy : Migration
    {
        private static readonly string[] Tables = ["privacy_notices", "consent_purposes", "consent_records", "data_subject_requests"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymised_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "ticket_comments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "consent_purposes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_purposes", x => x.id);
                    table.ForeignKey(
                        name: "fk_consent_purposes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_subject_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    identity_check = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handler_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    exported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    erased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_subject_requests", x => x.id);
                    table.CheckConstraint("ck_data_subject_requests_channel", "channel IN ('Portal', 'Staff')");
                    table.CheckConstraint("ck_data_subject_requests_state", "state IN ('Received', 'InProgress', 'Completed', 'Rejected', 'Withdrawn')");
                    table.CheckConstraint("ck_data_subject_requests_type", "type IN ('Access', 'Correction', 'Erasure', 'Restriction', 'Objection')");
                    table.ForeignKey(
                        name: "fk_data_subject_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_data_subject_requests_users_handler_id",
                        column: x => x.handler_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_data_subject_requests_users_subject_id",
                        column: x => x.subject_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_data_subject_requests_users_submitted_by_id",
                        column: x => x.submitted_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "privacy_notices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    body_ar = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: true),
                    body_en = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: true),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_privacy_notices", x => x.id);
                    table.CheckConstraint("ck_privacy_notices_state", "state IN ('Draft', 'Published', 'Superseded')");
                    table.ForeignKey(
                        name: "fk_privacy_notices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_privacy_notices_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_privacy_notices_users_published_by_id",
                        column: x => x.published_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visitor_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notice_version = table.Column<int>(type: "integer", nullable: true),
                    purpose_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recorded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_records", x => x.id);
                    table.CheckConstraint("ck_consent_records_action", "action IN ('NoticeAccepted', 'Granted', 'Withdrawn')");
                    table.CheckConstraint("ck_consent_records_channel", "channel IN ('Web', 'Chat', 'Anonymous', 'Staff', 'Api')");
                    table.CheckConstraint("ck_consent_records_what", "(action = 'NoticeAccepted' AND notice_id IS NOT NULL) OR (action <> 'NoticeAccepted' AND purpose_id IS NOT NULL)");
                    table.CheckConstraint("ck_consent_records_who", "user_id IS NOT NULL OR visitor_reference IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_consent_records_consent_purposes_purpose_id",
                        column: x => x.purpose_id,
                        principalTable: "consent_purposes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consent_records_privacy_notices_notice_id",
                        column: x => x.notice_id,
                        principalTable: "privacy_notices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consent_records_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consent_records_users_recorded_by_id",
                        column: x => x.recorded_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consent_records_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consent_purposes_tenant_id_key",
                table: "consent_purposes",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_notice_id",
                table: "consent_records",
                column: "notice_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_purpose_id",
                table: "consent_records",
                column: "purpose_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_recorded_by_id",
                table: "consent_records",
                column: "recorded_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_tenant_id_user_id_created_at",
                table: "consent_records",
                columns: new[] { "tenant_id", "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_user_id",
                table: "consent_records",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_handler_id",
                table: "data_subject_requests",
                column: "handler_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_subject_id",
                table: "data_subject_requests",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_submitted_by_id",
                table: "data_subject_requests",
                column: "submitted_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_tenant_id_sequence",
                table: "data_subject_requests",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_subject_requests_tenant_id_state_due_at",
                table: "data_subject_requests",
                columns: new[] { "tenant_id", "state", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_privacy_notices_created_by_id",
                table: "privacy_notices",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_privacy_notices_published_by_id",
                table: "privacy_notices",
                column: "published_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_privacy_notices_tenant_id",
                table: "privacy_notices",
                column: "tenant_id",
                unique: true,
                filter: "state = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ix_privacy_notices_tenant_id_version",
                table: "privacy_notices",
                columns: new[] { "tenant_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_privacy_notices_tenant_id1",
                table: "privacy_notices",
                column: "tenant_id",
                unique: true,
                filter: "state = 'Published'");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The consent history is proof of what people accepted: never changed. Rows may only be
            // removed with the tenant's other data (demo reset).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_consent_records_immutable() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    RAISE EXCEPTION 'consent records cannot be changed' USING ERRCODE = 'integrity_constraint_violation';
                END;
                $$;
                CREATE TRIGGER zelvo_consent_records_immutable BEFORE UPDATE ON consent_records
                    FOR EACH ROW EXECUTE FUNCTION zelvo_consent_records_immutable();
                """);

            // Comments stay a faithful record (task B4); task F5 adds the one other change allowed:
            // removing a comment's text for privacy, once, leaving everything else as it was.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zelvo_ticket_comments_immutable() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF ROW(NEW.id, NEW.tenant_id, NEW.ticket_id, NEW.visibility, NEW.language, NEW.author_id, NEW.by_staff, NEW.created_at)
                        IS NOT DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.ticket_id, OLD.visibility, OLD.language, OLD.author_id, OLD.by_staff, OLD.created_at) THEN
                        IF NEW.canned_response_id IS NULL AND NEW.body IS NOT DISTINCT FROM OLD.body AND NEW.erased_at IS NOT DISTINCT FROM OLD.erased_at THEN
                            RETURN NEW;
                        END IF;
                        IF OLD.erased_at IS NULL AND NEW.erased_at IS NOT NULL AND NEW.body = ''
                            AND NEW.canned_response_id IS NOT DISTINCT FROM OLD.canned_response_id THEN
                            RETURN NEW;
                        END IF;
                    END IF;
                    RAISE EXCEPTION 'ticket comments cannot be changed' USING ERRCODE = 'integrity_constraint_violation';
                END;
                $$;
                """);

            // The privacy jobs find tenants with a retention period or open data subject requests.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_privacy_job_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active' AND (
                        EXISTS (SELECT 1 FROM public.tenant_settings s WHERE s.tenant_id = t.id AND s.key = 'privacy.retention')
                        OR EXISTS (SELECT 1 FROM public.data_subject_requests r WHERE r.tenant_id = t.id AND r.state IN ('Received', 'InProgress')))
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_privacy_job_tenants();
                DROP TRIGGER zelvo_consent_records_immutable ON consent_records;
                DROP FUNCTION zelvo_consent_records_immutable();
                CREATE OR REPLACE FUNCTION zelvo_ticket_comments_immutable() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF NEW.canned_response_id IS NULL
                        AND ROW(NEW.id, NEW.tenant_id, NEW.ticket_id, NEW.visibility, NEW.body, NEW.language, NEW.author_id, NEW.by_staff, NEW.created_at)
                            IS NOT DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.ticket_id, OLD.visibility, OLD.body, OLD.language, OLD.author_id, OLD.by_staff, OLD.created_at) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'ticket comments cannot be changed' USING ERRCODE = 'integrity_constraint_violation';
                END;
                $$;
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "consent_records");

            migrationBuilder.DropTable(
                name: "data_subject_requests");

            migrationBuilder.DropTable(
                name: "consent_purposes");

            migrationBuilder.DropTable(
                name: "privacy_notices");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "anonymised_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "ticket_comments");
        }
    }
}
