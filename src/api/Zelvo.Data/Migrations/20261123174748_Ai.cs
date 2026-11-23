using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ai : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_category",
                table: "audit_events");

            migrationBuilder.CreateTable(
                name: "ai_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    api_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    api_key_ciphertext = table.Column<string>(type: "text", nullable: true),
                    api_key_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_test_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_providers", x => x.id);
                    table.CheckConstraint("ck_ai_providers_endpoint", "endpoint IS NULL OR endpoint ~ '^https?://'");
                    table.CheckConstraint("ck_ai_providers_endpoint_required", "kind NOT IN ('AzureOpenAI', 'OpenAICompatible') OR endpoint IS NOT NULL");
                    table.CheckConstraint("ck_ai_providers_kind", "kind IN ('Anthropic', 'OpenAI', 'AzureOpenAI', 'Gemini', 'OpenAICompatible')");
                    table.CheckConstraint("ck_ai_providers_status", "status IN ('Active', 'Disabled')");
                    table.ForeignKey(
                        name: "fk_ai_providers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_calls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    feature = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    injection_suspected = table.Column<bool>(type: "boolean", nullable: false),
                    masked_values = table.Column<int>(type: "integer", nullable: false),
                    prompt = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: true),
                    response = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_calls", x => x.id);
                    table.CheckConstraint("ck_ai_calls_outcome", "outcome IN ('Succeeded', 'Failed', 'Rejected', 'LimitReached')");
                    table.CheckConstraint("ck_ai_calls_provider_kind", "provider_kind IS NULL OR provider_kind IN ('Anthropic', 'OpenAI', 'AzureOpenAI', 'Gemini', 'OpenAICompatible')");
                    table.CheckConstraint("ck_ai_calls_tokens", "input_tokens >= 0 AND output_tokens >= 0");
                    table.ForeignKey(
                        name: "fk_ai_calls_ai_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "ai_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_calls_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_feature_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_feature_settings", x => new { x.tenant_id, x.feature });
                    table.CheckConstraint("ck_ai_feature_settings_enabled", "NOT enabled OR (provider_id IS NOT NULL AND model IS NOT NULL)");
                    table.CheckConstraint("ck_ai_feature_settings_feature", "feature ~ '^[a-z][a-zA-Z]*$'");
                    table.ForeignKey(
                        name: "fk_ai_feature_settings_ai_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "ai_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_feature_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_model_prices",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_per_million = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    output_per_million = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_model_prices", x => new { x.tenant_id, x.provider_id, x.model });
                    table.CheckConstraint("ck_ai_model_prices_range", "input_per_million >= 0 AND input_per_million <= 10000 AND output_per_million >= 0 AND output_per_million <= 10000");
                    table.ForeignKey(
                        name: "fk_ai_model_prices_ai_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "ai_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_model_prices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_category",
                table: "audit_events",
                sql: "category IN ('Create', 'Change', 'Delete', 'Admin', 'View', 'Security', 'Ai')");

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_provider_id",
                table: "ai_calls",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_tenant_id_id",
                table: "ai_calls",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_tenant_id_occurred_at",
                table: "ai_calls",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_text_retention",
                table: "ai_calls",
                column: "occurred_at",
                filter: "prompt IS NOT NULL OR response IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_feature_settings_provider_id",
                table: "ai_feature_settings",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_model_prices_provider_id",
                table: "ai_model_prices",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_providers_tenant_id_status",
                table: "ai_providers",
                columns: new[] { "tenant_id", "status" });

            // AI settings and calls belong to one tenant (see TenantIsolationMigrationExtensions).
            migrationBuilder.EnableTenantIsolation("ai_providers");
            migrationBuilder.EnableTenantIsolation("ai_feature_settings");
            migrationBuilder.EnableTenantIsolation("ai_model_prices");
            migrationBuilder.EnableTenantIsolation("ai_calls");

            // Retention (Ai:LogRetention): clears the masked prompt and answer of AI calls older
            // than p_before in every tenant; counts and cost stay for the usage dashboard.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_ai_call_text(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_count bigint;
                BEGIN
                    UPDATE public.ai_calls SET prompt = NULL, response = NULL
                    WHERE occurred_at < p_before AND (prompt IS NOT NULL OR response IS NOT NULL);
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_purge_ai_call_text(timestamptz);");
            migrationBuilder.DisableTenantIsolation("ai_calls");
            migrationBuilder.DisableTenantIsolation("ai_model_prices");
            migrationBuilder.DisableTenantIsolation("ai_feature_settings");
            migrationBuilder.DisableTenantIsolation("ai_providers");

            migrationBuilder.DropTable(
                name: "ai_calls");

            migrationBuilder.DropTable(
                name: "ai_feature_settings");

            migrationBuilder.DropTable(
                name: "ai_model_prices");

            migrationBuilder.DropTable(
                name: "ai_providers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_category",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_category",
                table: "audit_events",
                sql: "category IN ('Create', 'Change', 'Delete', 'Admin', 'View', 'Security')");
        }
    }
}
