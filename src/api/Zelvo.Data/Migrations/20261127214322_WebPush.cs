using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class WebPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "push",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    p256dh = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    auth = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    device_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_push_subscriptions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_push_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "web_push_keys",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    private_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_web_push_keys", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_web_push_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "push_deliveries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(503)", maxLength: 503, nullable: true),
                    tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
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
                    table.PrimaryKey("pk_push_deliveries", x => x.id);
                    table.CheckConstraint("ck_push_deliveries_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_push_deliveries_status", "status IN ('Pending', 'Sent', 'Failed')");
                    table.ForeignKey(
                        name: "fk_push_deliveries_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_push_deliveries_push_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "push_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_push_deliveries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_push_deliveries_users_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_push_deliveries_notification_id",
                table: "push_deliveries",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_push_deliveries_recipient_id",
                table: "push_deliveries",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_push_deliveries_subscription_id",
                table: "push_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_push_deliveries_tenant_id_created_at",
                table: "push_deliveries",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_push_deliveries_tenant_id_next_attempt_at",
                table: "push_deliveries",
                columns: new[] { "tenant_id", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_tenant_id_endpoint",
                table: "push_subscriptions",
                columns: new[] { "tenant_id", "endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_user_id",
                table: "push_subscriptions",
                column: "user_id");

            // Task J2: the three tables belong to their tenant (row-level security).
            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The worker also finds tenants with web push messages due.
            migrationBuilder.Sql(DueTenants(withPush: true));
            migrationBuilder.Sql(Purge(withPush: true));
        }

        private static readonly string[] Tables = ["web_push_keys", "push_subscriptions", "push_deliveries"];

        private const string PushDue = """

                OR EXISTS (
                    SELECT 1 FROM public.push_deliveries p
                    WHERE p.tenant_id = t.id AND p.status = 'Pending'
                      AND p.next_attempt_at <= now()
                      AND (p.lease_until IS NULL OR p.lease_until < now()))
            """;

        private const string PushPurge = """

                DELETE FROM public.push_deliveries WHERE status <> 'Pending' AND created_at < p_deliveries_before;
                GET DIAGNOSTICS v_push = ROW_COUNT;
            """;

        private static string DueTenants(bool withPush) => """
            CREATE OR REPLACE FUNCTION zelvo_notification_due_tenants()
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
                          AND (d.lease_until IS NULL OR d.lease_until < now()))
            """ + (withPush ? PushDue : "") + """
            )
            $$;
            """;

        private static string Purge(bool withPush) => """
            CREATE OR REPLACE FUNCTION zelvo_purge_notifications(p_deliveries_before timestamptz, p_notifications_before timestamptz)
            RETURNS bigint
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER
            SET search_path = pg_catalog, pg_temp
            AS $$
            DECLARE
                v_deliveries bigint;
                v_push bigint := 0;
                v_notifications bigint;
            BEGIN
                DELETE FROM public.notification_deliveries WHERE status <> 'Pending' AND created_at < p_deliveries_before;
                GET DIAGNOSTICS v_deliveries = ROW_COUNT;
            """ + (withPush ? PushPurge : "") + """

                DELETE FROM public.notifications WHERE status <> 'Pending' AND created_at < p_notifications_before;
                GET DIAGNOSTICS v_notifications = ROW_COUNT;
                RETURN v_deliveries + v_push + v_notifications;
            END;
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DueTenants(withPush: false));
            migrationBuilder.Sql(Purge(withPush: false));
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "push_deliveries");

            migrationBuilder.DropTable(
                name: "web_push_keys");

            migrationBuilder.DropTable(
                name: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "push",
                table: "notification_preferences");
        }
    }
}
