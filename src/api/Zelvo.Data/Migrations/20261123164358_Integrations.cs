using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    rate_limit_per_minute = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    key_prefix = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    keycloak_client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    keycloak_internal_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    service_account_subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_clients", x => x.id);
                    table.CheckConstraint("ck_api_clients_credentials", "(kind = 'ApiKey' AND key_prefix IS NOT NULL AND key_hash IS NOT NULL AND keycloak_client_id IS NULL) OR (kind = 'OAuthClient' AND key_prefix IS NULL AND key_hash IS NULL AND keycloak_client_id IS NOT NULL)");
                    table.CheckConstraint("ck_api_clients_key_prefix", "key_prefix ~ '^[a-z0-9]{12}$'");
                    table.CheckConstraint("ck_api_clients_kind", "kind IN ('ApiKey', 'OAuthClient')");
                    table.CheckConstraint("ck_api_clients_rate_limit", "rate_limit_per_minute BETWEEN 1 AND 100000");
                    table.CheckConstraint("ck_api_clients_status", "status IN ('Active', 'Revoked')");
                    table.ForeignKey(
                        name: "fk_api_clients_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_api_clients_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    api_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_id = table.Column<long>(type: "bigint", nullable: true),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    target = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    request_body = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    response_body = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_log", x => x.id);
                    table.CheckConstraint("ck_integration_log_direction", "direction IN ('Incoming', 'Outgoing')");
                    table.ForeignKey(
                        name: "fk_integration_log_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    events = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    disabled_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    secret_ciphertext = table.Column<string>(type: "text", nullable: false),
                    previous_secret_ciphertext = table.Column<string>(type: "text", nullable: true),
                    previous_secret_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_subscriptions", x => x.id);
                    table.CheckConstraint("ck_webhook_subscriptions_status", "status IN ('Active', 'Paused', 'Disabled')");
                    table.CheckConstraint("ck_webhook_subscriptions_url", "url ~ '^https?://'");
                    table.ForeignKey(
                        name: "fk_webhook_subscriptions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    manual = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_duration_ms = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.CheckConstraint("ck_webhook_deliveries_status", "status IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhook_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_role_id",
                table: "api_clients",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_tenant_id_key_prefix",
                table: "api_clients",
                columns: new[] { "tenant_id", "key_prefix" },
                unique: true,
                filter: "key_prefix IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_tenant_id_keycloak_client_id",
                table: "api_clients",
                columns: new[] { "tenant_id", "keycloak_client_id" },
                unique: true,
                filter: "keycloak_client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_tenant_id_service_account_subject",
                table: "api_clients",
                columns: new[] { "tenant_id", "service_account_subject" },
                unique: true,
                filter: "service_account_subject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_integration_log_tenant_id_api_client_id_id",
                table: "integration_log",
                columns: new[] { "tenant_id", "api_client_id", "id" },
                filter: "api_client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_integration_log_tenant_id_id",
                table: "integration_log",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_log_tenant_id_occurred_at",
                table: "integration_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_log_tenant_id_subscription_id_id",
                table: "integration_log",
                columns: new[] { "tenant_id", "subscription_id", "id" },
                filter: "subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_subscription_id_id",
                table: "webhook_deliveries",
                columns: new[] { "subscription_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_created_at",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_subscriptions_tenant_id_status",
                table: "webhook_subscriptions",
                columns: new[] { "tenant_id", "status" });

            // Integrations belong to one tenant (see TenantIsolationMigrationExtensions).
            migrationBuilder.EnableTenantIsolation("api_clients");
            migrationBuilder.EnableTenantIsolation("webhook_subscriptions");
            migrationBuilder.EnableTenantIsolation("webhook_deliveries");
            migrationBuilder.EnableTenantIsolation("integration_log");

            // The worker finds tenants with webhook deliveries that are due before a tenant is
            // set (row-level security hides every row until then). Ids and slugs only.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_webhook_due_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (
                        SELECT 1 FROM public.webhook_deliveries d
                        WHERE d.tenant_id = t.id AND d.status = 'Pending'
                          AND d.next_attempt_at <= now()
                          AND (d.lease_until IS NULL OR d.lease_until < now()))
                $$;
                """);

            // Retention (Integrations:LogRetention): removes integration log entries and finished
            // webhook deliveries older than p_before in every tenant; returns how many rows.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_integration_records(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_log bigint;
                    v_deliveries bigint;
                BEGIN
                    DELETE FROM public.integration_log WHERE occurred_at < p_before;
                    GET DIAGNOSTICS v_log = ROW_COUNT;
                    DELETE FROM public.webhook_deliveries WHERE status <> 'Pending' AND created_at < p_before;
                    GET DIAGNOSTICS v_deliveries = ROW_COUNT;
                    RETURN v_log + v_deliveries;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_integration_records(timestamptz);
                DROP FUNCTION zelvo_webhook_due_tenants();
                """);
            migrationBuilder.DisableTenantIsolation("integration_log");
            migrationBuilder.DisableTenantIsolation("webhook_deliveries");
            migrationBuilder.DisableTenantIsolation("webhook_subscriptions");
            migrationBuilder.DisableTenantIsolation("api_clients");

            migrationBuilder.DropTable(
                name: "api_clients");

            migrationBuilder.DropTable(
                name: "integration_log");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");
        }
    }
}
