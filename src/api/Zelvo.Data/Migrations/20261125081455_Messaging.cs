using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_comments_source",
                table: "ticket_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_jobs_kind",
                table: "ai_jobs");

            migrationBuilder.AddColumn<bool>(
                name: "notify_sms",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "notify_teams",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "notify_whats_app",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "messaging_contact_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "messaging_channel_id",
                table: "ticket_comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "messaging_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sender = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    directory_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    credential_ciphertext = table.Column<string>(type: "text", nullable: true),
                    credential_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    webhook_secret_ciphertext = table.Column<string>(type: "text", nullable: true),
                    new_senders = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    last_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_channels", x => x.id);
                    table.CheckConstraint("ck_messaging_channels_kind", "kind IN ('WhatsApp', 'Sms', 'Teams')");
                    table.CheckConstraint("ck_messaging_channels_kind_provider", "(kind = 'WhatsApp' AND provider = 'WhatsAppCloud') OR (kind = 'Sms' AND provider IN ('Twilio', 'GenericHttp')) OR (kind = 'Teams' AND provider = 'TeamsBot')");
                    table.CheckConstraint("ck_messaging_channels_new_senders", "new_senders IN ('Anyone', 'KnownUsers')");
                    table.CheckConstraint("ck_messaging_channels_provider", "provider IN ('WhatsAppCloud', 'Twilio', 'GenericHttp', 'TeamsBot')");
                    table.ForeignKey(
                        name: "fk_messaging_channels_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messaging_channels_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messaging_channels_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "voice_notes",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transcript = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    ai_call_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transcribed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_notes", x => x.comment_id);
                    table.CheckConstraint("ck_voice_notes_status", "status IN ('Pending', 'Done', 'Failed', 'Off')");
                    table.ForeignKey(
                        name: "fk_voice_notes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_voice_notes_ticket_attachments_attachment_id",
                        column: x => x.attachment_id,
                        principalTable: "ticket_attachments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_voice_notes_ticket_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_voice_notes_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messaging_contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    teams_service_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    teams_conversation_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    teams_user_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_contacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_messaging_contacts_messaging_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "messaging_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_messaging_contacts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messaging_contacts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "channel_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_messages", x => x.id);
                    table.CheckConstraint("ck_channel_messages_direction", "direction IN ('In', 'Out')");
                    table.CheckConstraint("ck_channel_messages_kind", "kind IN ('Text', 'Voice', 'Media', 'Template', 'Card', 'Action', 'Event')");
                    table.CheckConstraint("ck_channel_messages_language", "language IS NULL OR language IN ('en', 'ar')");
                    table.CheckConstraint("ck_channel_messages_status", "status IN ('Received', 'Ignored', 'Rejected', 'Pending', 'Sent', 'Delivered', 'Read', 'Failed')");
                    table.ForeignKey(
                        name: "fk_channel_messages_messaging_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "messaging_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_channel_messages_messaging_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "messaging_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_channel_messages_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_channel_messages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_channel_messages_ticket_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_channel_messages_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_messaging_contact_id",
                table: "tickets",
                column: "messaging_contact_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Sms', 'Teams', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_comments_source",
                table: "ticket_comments",
                sql: "source IN ('Web', 'Email', 'Chat', 'WhatsApp', 'Sms', 'Teams')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Sms', 'Teams', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_jobs_kind",
                table: "ai_jobs",
                sql: "kind IN ('Triage', 'Sentiment', 'Duplicates', 'Transcription')");

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_channel_id_provider_message_id_out",
                table: "channel_messages",
                columns: new[] { "channel_id", "provider_message_id" },
                unique: true,
                filter: "direction = 'Out' AND provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_comment_id",
                table: "channel_messages",
                column: "comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_contact_id",
                table: "channel_messages",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_notification_id_channel_id",
                table: "channel_messages",
                columns: new[] { "notification_id", "channel_id" },
                unique: true,
                filter: "notification_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_tenant_id_channel_id_created_at",
                table: "channel_messages",
                columns: new[] { "tenant_id", "channel_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_tenant_id_status_next_attempt_at",
                table: "channel_messages",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_channel_messages_ticket_id",
                table: "channel_messages",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_channels_category_id",
                table: "messaging_channels",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_channels_group_id",
                table: "messaging_channels",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_channels_tenant_id_kind",
                table: "messaging_channels",
                columns: new[] { "tenant_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_messaging_contacts_channel_id_address",
                table: "messaging_contacts",
                columns: new[] { "channel_id", "address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messaging_contacts_tenant_id_user_id",
                table: "messaging_contacts",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_messaging_contacts_user_id",
                table: "messaging_contacts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_voice_notes_attachment_id",
                table: "voice_notes",
                column: "attachment_id");

            migrationBuilder.CreateIndex(
                name: "ix_voice_notes_tenant_id_ticket_id",
                table: "voice_notes",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_voice_notes_ticket_id",
                table: "voice_notes",
                column: "ticket_id");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_messaging_contacts_messaging_contact_id",
                table: "tickets",
                column: "messaging_contact_id",
                principalTable: "messaging_contacts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Task E3: tenants with messages to send (ids and slugs only; the worker sets each
            // tenant before it reads its rows).
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_messaging_due_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (
                            SELECT 1 FROM public.channel_messages m
                            WHERE m.tenant_id = t.id AND m.status = 'Pending' AND m.next_attempt_at <= now()
                              AND (m.lease_until IS NULL OR m.lease_until < now()))
                $$;
                """);

            // The channel log is kept 30 days; messages still waiting stay.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_channel_messages(p_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_count bigint;
                BEGIN
                    DELETE FROM public.channel_messages WHERE status <> 'Pending' AND created_at < p_before;
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END;
                $$;
                """);
        }

        private static readonly string[] Tables = ["messaging_channels", "messaging_contacts", "channel_messages", "voice_notes"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_channel_messages(timestamptz);
                DROP FUNCTION zelvo_messaging_due_tenants();
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_messaging_contacts_messaging_contact_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "channel_messages");

            migrationBuilder.DropTable(
                name: "voice_notes");

            migrationBuilder.DropTable(
                name: "messaging_contacts");

            migrationBuilder.DropTable(
                name: "messaging_channels");

            migrationBuilder.DropIndex(
                name: "ix_tickets_messaging_contact_id",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_comments_source",
                table: "ticket_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_jobs_kind",
                table: "ai_jobs");

            migrationBuilder.DropColumn(
                name: "notify_sms",
                table: "users");

            migrationBuilder.DropColumn(
                name: "notify_teams",
                table: "users");

            migrationBuilder.DropColumn(
                name: "notify_whats_app",
                table: "users");

            migrationBuilder.DropColumn(
                name: "messaging_contact_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "messaging_channel_id",
                table: "ticket_comments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_channel",
                table: "tickets",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_comments_source",
                table: "ticket_comments",
                sql: "source IN ('Web', 'Email', 'Chat')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_happiness_ratings_channel",
                table: "happiness_ratings",
                sql: "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent', 'Proactive')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_jobs_kind",
                table: "ai_jobs",
                sql: "kind IN ('Triage', 'Sentiment', 'Duplicates')");
        }
    }
}
