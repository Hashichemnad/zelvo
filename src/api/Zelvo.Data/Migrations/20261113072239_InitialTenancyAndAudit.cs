using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenancyAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_slug", "slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'");
                    table.CheckConstraint("ck_tenants_status", "status IN ('Active', 'Suspended')");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    previous_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_actor_type", "actor_type IN ('System', 'User', 'Anonymous', 'ApiClient', 'AiAgent')");
                    table.CheckConstraint("ck_audit_events_category", "category IN ('Create', 'Change', 'Delete', 'Admin', 'View', 'Security')");
                    table.ForeignKey(
                        name: "fk_audit_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_entity_type_entity_id",
                table: "audit_events",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_occurred_at",
                table: "audit_events",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_sequence",
                table: "audit_events",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            // ---- Hand-written: tenant isolation and the tamper-evident audit log ----

            // The tenant of the current connection, set by TenantConnectionInterceptor.
            // NULL when unset, so tenant-owned tables show nothing (fail closed).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_current_tenant_id() RETURNS uuid
                LANGUAGE sql STABLE PARALLEL SAFE
                AS $$ SELECT NULLIF(current_setting('zelvo.tenant_id', true), '')::uuid $$;
                """);

            // Tenants: the application role reads and updates only its own tenant and cannot
            // create or delete tenants. Not FORCEd, so zelvo_resolve_tenant (owned by the
            // migration owner) can look a tenant up by slug before a tenant is set.
            migrationBuilder.Sql("""
                ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_self_select ON tenants FOR SELECT
                    USING (id = zelvo_current_tenant_id());
                CREATE POLICY tenant_self_update ON tenants FOR UPDATE
                    USING (id = zelvo_current_tenant_id())
                    WITH CHECK (id = zelvo_current_tenant_id());

                CREATE FUNCTION zelvo_resolve_tenant(p_slug text)
                RETURNS TABLE (id uuid, slug text, status text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$ SELECT t.id, t.slug::text, t.status::text FROM public.tenants t WHERE t.slug = p_slug $$;
                """);

            // Audit log: hash of one entry over a canonical JSON array of its fields and the
            // previous entry's hash. Changing this function breaks verification of existing
            // entries; a new format needs a new version number (the first element).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_audit_hash(e audit_events) RETURNS bytea
                LANGUAGE sql STABLE PARALLEL SAFE
                AS $$
                    SELECT sha256(convert_to(jsonb_build_array(
                        1,
                        e.tenant_id,
                        e.sequence,
                        to_char(e.occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                        e.category,
                        e.action,
                        e.actor_type,
                        e.actor_id,
                        e.actor_name,
                        e.entity_type,
                        e.entity_id,
                        e.changes,
                        e.metadata,
                        encode(e.previous_hash, 'hex'))::text, 'UTF8'))
                $$;

                -- Numbers, timestamps and chains every new entry. The advisory lock serialises
                -- writers of one tenant, so the chain has no forks or gaps.
                CREATE FUNCTION zelvo_audit_events_chain() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    last_sequence bigint;
                    last_hash bytea;
                BEGIN
                    PERFORM pg_advisory_xact_lock(hashtextextended('zelvo.audit:' || NEW.tenant_id::text, 0));
                    SELECT a.sequence, a.hash INTO last_sequence, last_hash
                      FROM audit_events a
                     WHERE a.tenant_id = NEW.tenant_id
                     ORDER BY a.sequence DESC
                     LIMIT 1;
                    NEW.sequence := COALESCE(last_sequence, 0) + 1;
                    NEW.previous_hash := last_hash;
                    NEW.occurred_at := clock_timestamp();
                    NEW.hash := zelvo_audit_hash(NEW);
                    RETURN NEW;
                END
                $$;

                CREATE TRIGGER audit_events_chain BEFORE INSERT ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION zelvo_audit_events_chain();

                -- Append-only, for every role including the owner.
                CREATE FUNCTION zelvo_audit_events_append_only() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only' USING ERRCODE = 'insufficient_privilege';
                END
                $$;

                CREATE TRIGGER audit_events_no_update_delete BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION zelvo_audit_events_append_only();
                CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION zelvo_audit_events_append_only();

                -- The application reads and adds entries of its own tenant only; there is no
                -- update or delete policy.
                ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation_select ON audit_events FOR SELECT
                    USING (tenant_id = zelvo_current_tenant_id());
                CREATE POLICY tenant_isolation_insert ON audit_events FOR INSERT
                    WITH CHECK (tenant_id = zelvo_current_tenant_id());

                -- Recomputes a tenant's chain. Reports the first entry that was changed, removed
                -- or reordered, and the newest entry's sequence and hash.
                CREATE FUNCTION zelvo_verify_audit_chain(p_tenant_id uuid)
                RETURNS TABLE (events_checked bigint, first_invalid_sequence bigint, head_sequence bigint, head_hash bytea)
                LANGUAGE plpgsql STABLE
                AS $$
                #variable_conflict use_column
                DECLARE
                    e audit_events;
                    expected_sequence bigint := 1;
                    expected_previous bytea := NULL;
                BEGIN
                    FOR e IN SELECT * FROM audit_events a WHERE a.tenant_id = p_tenant_id ORDER BY a.sequence LOOP
                        IF e.sequence <> expected_sequence
                           OR e.previous_hash IS DISTINCT FROM expected_previous
                           OR e.hash IS DISTINCT FROM zelvo_audit_hash(e) THEN
                            RETURN QUERY SELECT expected_sequence - 1, expected_sequence, NULL::bigint, NULL::bytea;
                            RETURN;
                        END IF;
                        expected_previous := e.hash;
                        expected_sequence := expected_sequence + 1;
                    END LOOP;
                    RETURN QUERY SELECT expected_sequence - 1, NULL::bigint,
                        NULLIF(expected_sequence - 1, 0), expected_previous;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_verify_audit_chain(uuid);
                DROP TRIGGER audit_events_no_truncate ON audit_events;
                DROP TRIGGER audit_events_no_update_delete ON audit_events;
                DROP TRIGGER audit_events_chain ON audit_events;
                DROP FUNCTION zelvo_audit_events_append_only();
                DROP FUNCTION zelvo_audit_events_chain();
                DROP FUNCTION zelvo_audit_hash(audit_events);
                DROP FUNCTION zelvo_resolve_tenant(text);
                """);

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.Sql("DROP FUNCTION zelvo_current_tenant_id();");
        }
    }
}
