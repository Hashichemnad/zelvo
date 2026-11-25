using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Connectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings");

            migrationBuilder.CreateTable(
                name: "connectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    credential_ciphertext = table.Column<string>(type: "text", nullable: true),
                    credential_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    sync_minutes = table.Column<int>(type: "integer", nullable: false),
                    system_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    next_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_sync_stats = table.Column<string>(type: "jsonb", nullable: true),
                    sync_cursor = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connectors", x => x.id);
                    table.CheckConstraint("ck_connectors_kind", "kind IN ('EntraId', 'Ldap', 'Dynamics365', 'Salesforce', 'HubSpot', 'Zoho', 'Rest')");
                    table.CheckConstraint("ck_connectors_sync_minutes", "sync_minutes BETWEEN 5 AND 10080");
                    table.ForeignKey(
                        name: "fk_connectors_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_connectors_users_system_user_id",
                        column: x => x.system_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mapping = table.Column<string>(type: "jsonb", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolve_on_clear = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    system_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_alert_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitoring_sources", x => x.id);
                    table.CheckConstraint("ck_monitoring_sources_format", "format IN ('Alertmanager', 'Generic')");
                    table.ForeignKey(
                        name: "fk_monitoring_sources_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monitoring_sources_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monitoring_sources_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monitoring_sources_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monitoring_sources_users_system_user_id",
                        column: x => x.system_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "connector_calls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    operation = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connector_calls", x => x.id);
                    table.CheckConstraint("ck_connector_calls_kind", "kind IN ('CrmCase', 'Rest')");
                    table.CheckConstraint("ck_connector_calls_status", "status IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "fk_connector_calls_connectors_connector_id",
                        column: x => x.connector_id,
                        principalTable: "connectors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_connector_calls_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_connector_calls_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "connector_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    zelvo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    gone = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connector_links", x => x.id);
                    table.CheckConstraint("ck_connector_links_entity", "entity IN ('User', 'Department', 'CustomerGroup', 'Ticket')");
                    table.ForeignKey(
                        name: "fk_connector_links_connectors_connector_id",
                        column: x => x.connector_id,
                        principalTable: "connectors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_connector_links_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_alerts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitoring_alerts", x => x.id);
                    table.CheckConstraint("ck_monitoring_alerts_state", "state IN ('Firing', 'Resolved')");
                    table.ForeignKey(
                        name: "fk_monitoring_alerts_monitoring_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "monitoring_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_monitoring_alerts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monitoring_alerts_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Sms', 'Teams', 'Monitoring', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Sms', 'Teams', 'Monitoring', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.CreateIndex(
                name: "ix_connector_calls_connector_id",
                table: "connector_calls",
                column: "connector_id");

            migrationBuilder.CreateIndex(
                name: "ix_connector_calls_tenant_id_connector_id_created_at",
                table: "connector_calls",
                columns: new[] { "tenant_id", "connector_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_connector_calls_tenant_id_status_next_attempt_at",
                table: "connector_calls",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_connector_calls_ticket_id",
                table: "connector_calls",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_connector_links_connector_id_entity_external_id",
                table: "connector_links",
                columns: new[] { "connector_id", "entity", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_links_connector_id_entity_zelvo_id",
                table: "connector_links",
                columns: new[] { "connector_id", "entity", "zelvo_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_links_tenant_id_entity_zelvo_id",
                table: "connector_links",
                columns: new[] { "tenant_id", "entity", "zelvo_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connectors_active_next_sync_at",
                table: "connectors",
                columns: new[] { "active", "next_sync_at" });

            migrationBuilder.CreateIndex(
                name: "ix_connectors_system_user_id",
                table: "connectors",
                column: "system_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_connectors_tenant_id_kind",
                table: "connectors",
                columns: new[] { "tenant_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_alerts_source_id_key",
                table: "monitoring_alerts",
                columns: new[] { "source_id", "key" },
                unique: true,
                filter: "state = 'Firing'");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_alerts_tenant_id_ticket_id",
                table: "monitoring_alerts",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_alerts_ticket_id",
                table: "monitoring_alerts",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_sources_category_id",
                table: "monitoring_sources",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_sources_group_id",
                table: "monitoring_sources",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_sources_location_id",
                table: "monitoring_sources",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_sources_system_user_id",
                table: "monitoring_sources",
                column: "system_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_sources_tenant_id",
                table: "monitoring_sources",
                column: "tenant_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Task E7: tenants with connector syncs or calls due (ids and slugs only).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_connector_due_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND (EXISTS (
                            SELECT 1 FROM public.connectors c
                            WHERE c.tenant_id = t.id AND c.active AND c.kind <> 'Rest' AND c.next_sync_at <= now()
                              AND (c.lease_until IS NULL OR c.lease_until < now()))
                        OR EXISTS (
                            SELECT 1 FROM public.connector_calls k
                            WHERE k.tenant_id = t.id AND k.status = 'Pending' AND k.next_attempt_at <= now()
                              AND (k.lease_until IS NULL OR k.lease_until < now())))
                $$;
                """);

            // Finished calls are kept 30 days (their bodies are in the integration log).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_connector_calls(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_count bigint;
                BEGIN
                    DELETE FROM public.connector_calls WHERE status <> 'Pending' AND created_at < p_before;
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END;
                $$;
                """);
        }

        private static readonly string[] Tables = ["connectors", "connector_links", "connector_calls", "monitoring_sources", "monitoring_alerts"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_connector_calls(timestamptz);
                DROP FUNCTION zelvo_connector_due_tenants();
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "connector_calls");

            migrationBuilder.DropTable(
                name: "connector_links");

            migrationBuilder.DropTable(
                name: "monitoring_alerts");

            migrationBuilder.DropTable(
                name: "connectors");

            migrationBuilder.DropTable(
                name: "monitoring_sources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Sms', 'Teams', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Sms', 'Teams', 'Api', 'AiAgent', 'Proactive')");
        }
    }
}
