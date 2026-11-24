using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_sessions_state",
                table: "chat_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_messages_sender",
                table: "chat_messages");

            migrationBuilder.AddColumn<bool>(
                name: "virtual_agent",
                table: "chat_widgets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid[]>(
                name: "citations",
                table: "chat_messages",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_duplicate_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_duplicate_candidates", x => x.id);
                    table.CheckConstraint("ck_ai_duplicate_candidates_confidence", "confidence BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_ai_duplicate_candidates_other", "ticket_id <> candidate_id");
                    table.CheckConstraint("ck_ai_duplicate_candidates_status", "status IN ('Pending', 'Linked', 'Dismissed')");
                    table.ForeignKey(
                        name: "fk_ai_duplicate_candidates_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_duplicate_candidates_tickets_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_duplicate_candidates_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_duplicate_candidates_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_jobs", x => x.id);
                    table.CheckConstraint("ck_ai_jobs_kind", "kind IN ('Triage', 'Sentiment', 'Duplicates')");
                    table.CheckConstraint("ck_ai_jobs_status", "status IN ('Pending', 'Done', 'Failed')");
                    table.ForeignKey(
                        name: "fk_ai_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_jobs_ticket_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_jobs_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_ticket_insights",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sentiment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sentiment_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    sentiment_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sentiment_comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    frustrated_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    summary_language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    summary_basis = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    summary_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_ticket_insights", x => x.ticket_id);
                    table.CheckConstraint("ck_ai_ticket_insights_language", "summary_language IS NULL OR summary_language IN ('en', 'ar')");
                    table.CheckConstraint("ck_ai_ticket_insights_sentiment", "sentiment IS NULL OR sentiment IN ('Positive', 'Neutral', 'Negative', 'Frustrated')");
                    table.ForeignKey(
                        name: "fk_ai_ticket_insights_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_ticket_insights_ticket_comments_sentiment_comment_id",
                        column: x => x.sentiment_comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_ticket_insights_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_translations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    source_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    text = table.Column<string>(type: "character varying(24000)", maxLength: 24000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_translations", x => new { x.tenant_id, x.source, x.source_id, x.language });
                    table.CheckConstraint("ck_ai_translations_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_ai_translations_source", "source IN ('Ticket', 'Comment', 'ChatMessage')");
                    table.ForeignKey(
                        name: "fk_ai_translations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_triage_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    impact = table.Column<int>(type: "integer", nullable: true),
                    urgency = table.Column<int>(type: "integer", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ai_call_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    final_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_impact = table.Column<int>(type: "integer", nullable: true),
                    final_urgency = table.Column<int>(type: "integer", nullable: true),
                    final_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_triage_suggestions", x => x.id);
                    table.CheckConstraint("ck_ai_triage_suggestions_confidence", "confidence BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_ai_triage_suggestions_levels", "(impact IS NULL OR impact BETWEEN 1 AND 3) AND (urgency IS NULL OR urgency BETWEEN 1 AND 3) AND (final_impact IS NULL OR final_impact BETWEEN 1 AND 3) AND (final_urgency IS NULL OR final_urgency BETWEEN 1 AND 3)");
                    table.CheckConstraint("ck_ai_triage_suggestions_status", "status IN ('Pending', 'Accepted', 'Corrected', 'Dismissed', 'AutoApplied', 'Outdated')");
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_categories_final_category_id",
                        column: x => x.final_category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_support_groups_final_group_id",
                        column: x => x.final_group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_triage_suggestions_users_decided_by_id",
                        column: x => x.decided_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_sessions_state",
                table: "chat_sessions",
                sql: "state IN ('Waiting', 'Assistant', 'Active', 'Ended')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_messages_sender",
                table: "chat_messages",
                sql: "sender IN ('Visitor', 'Agent', 'System', 'Assistant')");

            migrationBuilder.CreateIndex(
                name: "ix_ai_duplicate_candidates_candidate_id",
                table: "ai_duplicate_candidates",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_duplicate_candidates_decided_by_id",
                table: "ai_duplicate_candidates",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_duplicate_candidates_tenant_id_ticket_id_candidate_id",
                table: "ai_duplicate_candidates",
                columns: new[] { "tenant_id", "ticket_id", "candidate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_duplicate_candidates_ticket_id",
                table: "ai_duplicate_candidates",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_jobs_comment_id",
                table: "ai_jobs",
                column: "comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_jobs_due",
                table: "ai_jobs",
                column: "next_attempt_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_ai_jobs_tenant_id_status_next_attempt_at",
                table: "ai_jobs",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_jobs_ticket_id",
                table: "ai_jobs",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_ticket_insights_sentiment_comment_id",
                table: "ai_ticket_insights",
                column: "sentiment_comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_ticket_insights_tenant_id_frustrated_since",
                table: "ai_ticket_insights",
                columns: new[] { "tenant_id", "frustrated_since" },
                filter: "frustrated_since IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_category_id",
                table: "ai_triage_suggestions",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_decided_by_id",
                table: "ai_triage_suggestions",
                column: "decided_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_final_category_id",
                table: "ai_triage_suggestions",
                column: "final_category_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_final_group_id",
                table: "ai_triage_suggestions",
                column: "final_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_group_id",
                table: "ai_triage_suggestions",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_tenant_id_decided_at",
                table: "ai_triage_suggestions",
                columns: new[] { "tenant_id", "decided_at" },
                filter: "decided_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_tenant_id_ticket_id",
                table: "ai_triage_suggestions",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_triage_suggestions_ticket_id",
                table: "ai_triage_suggestions",
                column: "ticket_id");

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The worker finds tenants with AI jobs due before a tenant is set (row-level
            // security hides every row until then). Ids and slugs only.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_ai_job_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (
                            SELECT 1 FROM public.ai_jobs j
                            WHERE j.tenant_id = t.id AND j.status = 'Pending' AND j.next_attempt_at <= now()
                              AND (j.lease_until IS NULL OR j.lease_until < now()))
                $$;
                """);

            // Finished jobs are removed after a week (Ai:JobRetention), in every tenant.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_ai_jobs(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_count bigint;
                BEGIN
                    DELETE FROM public.ai_jobs WHERE status <> 'Pending' AND finished_at < p_before;
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END;
                $$;
                """);
        }

        private static readonly string[] Tables = ["ai_jobs", "ai_triage_suggestions", "ai_ticket_insights", "ai_duplicate_candidates", "ai_translations"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_ai_jobs(timestamptz);
                DROP FUNCTION zelvo_ai_job_tenants();
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "ai_duplicate_candidates");

            migrationBuilder.DropTable(
                name: "ai_jobs");

            migrationBuilder.DropTable(
                name: "ai_ticket_insights");

            migrationBuilder.DropTable(
                name: "ai_translations");

            migrationBuilder.DropTable(
                name: "ai_triage_suggestions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_sessions_state",
                table: "chat_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_messages_sender",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "virtual_agent",
                table: "chat_widgets");

            migrationBuilder.DropColumn(
                name: "citations",
                table: "chat_messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_sessions_state",
                table: "chat_sessions",
                sql: "state IN ('Waiting', 'Active', 'Ended')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_messages_sender",
                table: "chat_messages",
                sql: "sender IN ('Visitor', 'Agent', 'System')");
        }
    }
}
