using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => new { x.user_id, x.event_type });
                    table.ForeignKey(
                        name: "fk_notification_preferences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_templates",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    action_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    action_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    body_ar = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    body_en = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    in_app_ar = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    in_app_en = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    subject_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    subject_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_templates", x => new { x.tenant_id, x.event_type });
                    table.ForeignKey(
                        name: "fk_notification_templates_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    link_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    article_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sla_alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_language", "language IS NULL OR language IN ('en', 'ar')");
                    table.CheckConstraint("ck_notifications_link_path", "link_path IS NULL OR link_path ~ '^/[A-Za-z0-9/_?=&.-]*$'");
                    table.CheckConstraint("ck_notifications_status", "status IN ('Pending', 'Delivered', 'Suppressed')");
                    table.ForeignKey(
                        name: "fk_notifications_kb_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_sla_alerts_sla_alert_id",
                        column: x => x.sla_alert_id,
                        principalTable: "sla_alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notifications_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_users_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    html_body = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    text_body = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    manual = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.CheckConstraint("ck_notification_deliveries_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_notification_deliveries_status", "status IN ('Pending', 'Sent', 'Failed')");
                    table.ForeignKey(
                        name: "fk_notification_deliveries_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_users_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_notification_id",
                table: "notification_deliveries",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_recipient_id",
                table: "notification_deliveries",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_created_at",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_id",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_next_attempt_at",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_tenant_id",
                table: "notification_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_article_id",
                table: "notifications",
                column: "article_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                table: "notifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_pending",
                table: "notifications",
                columns: new[] { "tenant_id", "created_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_id_id",
                table: "notifications",
                columns: new[] { "recipient_id", "id" },
                filter: "status = 'Delivered'");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_sla_alert_id",
                table: "notifications",
                column: "sla_alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_ticket_id",
                table: "notifications",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_unread",
                table: "notifications",
                column: "recipient_id",
                filter: "status = 'Delivered' AND read_at IS NULL");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The worker finds tenants with notifications to render or emails due before a tenant
            // is set (row-level security hides every row until then). Ids and slugs only.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_notification_due_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND (EXISTS (
                            SELECT 1 FROM public.notifications n
                            WHERE n.tenant_id = t.id AND n.status = 'Pending'
                              AND (n.lease_until IS NULL OR n.lease_until < now()))
                        OR EXISTS (
                            SELECT 1 FROM public.notification_deliveries d
                            WHERE d.tenant_id = t.id AND d.status = 'Pending'
                              AND d.next_attempt_at <= now()
                              AND (d.lease_until IS NULL OR d.lease_until < now())))
                $$;
                """);

            // Retention (Notifications:DeliveryRetention and Notifications:InAppRetention): removes
            // finished emails and in-app notifications older than the given times in every tenant.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_notifications(p_deliveries_before timestamptz, p_notifications_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_deliveries bigint;
                    v_notifications bigint;
                BEGIN
                    DELETE FROM public.notification_deliveries WHERE status <> 'Pending' AND created_at < p_deliveries_before;
                    GET DIAGNOSTICS v_deliveries = ROW_COUNT;
                    DELETE FROM public.notifications WHERE status <> 'Pending' AND created_at < p_notifications_before;
                    GET DIAGNOSTICS v_notifications = ROW_COUNT;
                    RETURN v_deliveries + v_notifications;
                END;
                $$;
                """);
        }

        private static readonly string[] Tables = ["notifications", "notification_deliveries", "notification_templates", "notification_preferences"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_notifications(timestamptz, timestamptz);
                DROP FUNCTION zelvo_notification_due_tenants();
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notification_templates");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
