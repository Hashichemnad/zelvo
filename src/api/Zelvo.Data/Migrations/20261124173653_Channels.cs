using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Channels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "mailbox_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "chat_session_id",
                table: "ticket_comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_in_reply_to",
                table: "ticket_comments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_message_id",
                table: "ticket_comments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "mailbox_id",
                table: "ticket_comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "ticket_comments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Web");

            migrationBuilder.AddColumn<Guid>(
                name: "comment_id",
                table: "ticket_attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "chat_limit",
                table: "support_groups",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<Guid>(
                name: "comment_id",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_widgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allowed_origins = table.Column<string[]>(type: "text[]", nullable: false),
                    on_portal = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    greeting_ar = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    greeting_en = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    name_ar = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_widgets", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_widgets_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_widgets_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_widgets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mailboxes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    auth = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    imap_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    imap_port = table.Column<int>(type: "integer", nullable: true),
                    imap_security = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    smtp_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: true),
                    smtp_security = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    username = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    client_id = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    directory_id = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    credential_ciphertext = table.Column<string>(type: "text", nullable: true),
                    credential_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    o_auth_pending = table.Column<string>(type: "text", nullable: true),
                    signed_in_as = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    new_senders = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    allowed_domains = table.Column<string[]>(type: "text[]", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sync_state = table.Column<string>(type: "jsonb", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_check_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mailboxes", x => x.id);
                    table.CheckConstraint("ck_mailboxes_auth", "auth IN ('Password', 'ClientSecret', 'ServiceAccount', 'OAuthSignIn')");
                    table.CheckConstraint("ck_mailboxes_imap_security", "imap_security IN ('Auto', 'SslOnConnect', 'StartTls', 'None')");
                    table.CheckConstraint("ck_mailboxes_new_senders", "new_senders IN ('Anyone', 'AllowedDomains', 'KnownUsers')");
                    table.CheckConstraint("ck_mailboxes_ports", "(imap_port IS NULL OR imap_port BETWEEN 1 AND 65535) AND (smtp_port IS NULL OR smtp_port BETWEEN 1 AND 65535)");
                    table.CheckConstraint("ck_mailboxes_provider", "provider IN ('Imap', 'Microsoft365', 'Gmail')");
                    table.CheckConstraint("ck_mailboxes_smtp_security", "smtp_security IN ('Auto', 'SslOnConnect', 'StartTls', 'None')");
                    table.CheckConstraint("ck_mailboxes_status", "status IN ('Active', 'Disabled')");
                    table.ForeignKey(
                        name: "fk_mailboxes_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mailboxes_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mailboxes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    widget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visitor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visitor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    visitor_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    visitor_token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    origin = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_visitor_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_sessions", x => x.id);
                    table.CheckConstraint("ck_chat_sessions_agent", "state <> 'Active' OR agent_id IS NOT NULL");
                    table.CheckConstraint("ck_chat_sessions_ended_by", "ended_by IS NULL OR ended_by IN ('Visitor', 'Agent', 'System')");
                    table.CheckConstraint("ck_chat_sessions_language", "language IN ('en', 'ar')");
                    table.CheckConstraint("ck_chat_sessions_state", "state IN ('Waiting', 'Active', 'Ended')");
                    table.ForeignKey(
                        name: "fk_chat_sessions_chat_widgets_widget_id",
                        column: x => x.widget_id,
                        principalTable: "chat_widgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_sessions_support_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_sessions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_chat_sessions_users_agent_id",
                        column: x => x.agent_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_sessions_users_visitor_user_id",
                        column: x => x.visitor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbound_emails",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    from_address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_emails", x => x.id);
                    table.CheckConstraint("ck_inbound_emails_outcome", "outcome IN ('Created', 'Added', 'FollowUp', 'Rejected', 'Ignored', 'Failed')");
                    table.ForeignKey(
                        name: "fk_inbound_emails_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_inbound_emails_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inbound_emails_ticket_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_inbound_emails_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "outbound_emails",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    in_reply_to = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    references = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    html_body = table.Column<string>(type: "text", nullable: false),
                    text_body = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_emails", x => x.id);
                    table.CheckConstraint("ck_outbound_emails_status", "status IN ('Pending', 'Sent', 'Failed')");
                    table.ForeignKey(
                        name: "fk_outbound_emails_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outbound_emails_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_outbound_emails_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outbound_emails_ticket_comments_comment_id",
                        column: x => x.comment_id,
                        principalTable: "ticket_comments",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_outbound_emails_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.CheckConstraint("ck_chat_messages_sender", "sender IN ('Visitor', 'Agent', 'System')");
                    table.ForeignKey(
                        name: "fk_chat_messages_chat_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "chat_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_messages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_messages_users_author_id",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_mailbox_id",
                table: "tickets",
                column: "mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_tenant_id_email_message_id",
                table: "ticket_comments",
                columns: new[] { "tenant_id", "email_message_id" },
                filter: "email_message_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_comments_channel_public",
                table: "ticket_comments",
                sql: "source = 'Web' OR visibility = 'Public'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_comments_source",
                table: "ticket_comments",
                sql: "source IN ('Web', 'Email', 'Chat')");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_attachments_comment_id",
                table: "ticket_attachments",
                column: "comment_id",
                filter: "comment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_attachments_comment",
                table: "ticket_attachments",
                sql: "comment_id IS NULL OR ticket_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_support_groups_chat_limit",
                table: "support_groups",
                sql: "chat_limit BETWEEN 1 AND 20");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_author_id",
                table: "chat_messages",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_session_id",
                table: "chat_messages",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_tenant_id_session_id_sequence",
                table: "chat_messages",
                columns: new[] { "tenant_id", "session_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_agent_id",
                table: "chat_sessions",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_group_id",
                table: "chat_sessions",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_state_last_visitor_activity_at",
                table: "chat_sessions",
                columns: new[] { "state", "last_visitor_activity_at" },
                filter: "state <> 'Ended'");

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_tenant_id_agent_id_state",
                table: "chat_sessions",
                columns: new[] { "tenant_id", "agent_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_tenant_id_group_id_state",
                table: "chat_sessions",
                columns: new[] { "tenant_id", "group_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_tenant_id_visitor_user_id",
                table: "chat_sessions",
                columns: new[] { "tenant_id", "visitor_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_ticket_id",
                table: "chat_sessions",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_visitor_user_id",
                table: "chat_sessions",
                column: "visitor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_widget_id",
                table: "chat_sessions",
                column: "widget_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_widgets_category_id",
                table: "chat_widgets",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_widgets_group_id",
                table: "chat_widgets",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_widgets_tenant_id",
                table: "chat_widgets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_comment_id",
                table: "inbound_emails",
                column: "comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_mailbox_id_provider_message_id",
                table: "inbound_emails",
                columns: new[] { "mailbox_id", "provider_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_tenant_id_mailbox_id_received_at",
                table: "inbound_emails",
                columns: new[] { "tenant_id", "mailbox_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_tenant_id_message_id",
                table: "inbound_emails",
                columns: new[] { "tenant_id", "message_id" },
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_ticket_id",
                table: "inbound_emails",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_category_id",
                table: "mailboxes",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_group_id",
                table: "mailboxes",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_status_next_check_at",
                table: "mailboxes",
                columns: new[] { "status", "next_check_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_tenant_id_address",
                table: "mailboxes",
                columns: new[] { "tenant_id", "address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_comment_id",
                table: "outbound_emails",
                column: "comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_mailbox_id",
                table: "outbound_emails",
                column: "mailbox_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_notification_id",
                table: "outbound_emails",
                column: "notification_id",
                unique: true,
                filter: "notification_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_tenant_id_mailbox_id_created_at",
                table: "outbound_emails",
                columns: new[] { "tenant_id", "mailbox_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_tenant_id_status_next_attempt_at",
                table: "outbound_emails",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_ticket_id",
                table: "outbound_emails",
                column: "ticket_id");

            migrationBuilder.AddForeignKey(
                name: "fk_ticket_attachments_ticket_comments_comment_id",
                table: "ticket_attachments",
                column: "comment_id",
                principalTable: "ticket_comments",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_mailboxes_mailbox_id",
                table: "tickets",
                column: "mailbox_id",
                principalTable: "mailboxes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            foreach (var table in Tables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // The worker finds tenants with mailboxes to read or replies to send before a tenant is
            // set (row-level security hides every row until then). Ids and slugs only.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_mail_due_tenants()
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND (EXISTS (
                            SELECT 1 FROM public.mailboxes m
                            WHERE m.tenant_id = t.id AND m.status = 'Active' AND m.next_check_at <= now()
                              AND (m.lease_until IS NULL OR m.lease_until < now()))
                        OR EXISTS (
                            SELECT 1 FROM public.outbound_emails o
                            WHERE o.tenant_id = t.id AND o.status = 'Pending' AND o.next_attempt_at <= now()
                              AND (o.lease_until IS NULL OR o.lease_until < now())))
                $$;
                """);

            // Chats the visitor left: tenants with chats not ended whose visitor was last active
            // before the given time.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_idle_chat_tenants(p_before timestamptz)
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (
                            SELECT 1 FROM public.chat_sessions c
                            WHERE c.tenant_id = t.id AND c.state <> 'Ended' AND c.last_visitor_activity_at < p_before)
                $$;
                """);

            // Retention (Channels:InboundLogRetention, Channels:OutboundRetention): removes old
            // mailbox log rows and finished outgoing emails in every tenant.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_purge_mail_logs(p_inbound_before timestamptz, p_outbound_before timestamptz)
                RETURNS bigint
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    v_inbound bigint;
                    v_outbound bigint;
                BEGIN
                    DELETE FROM public.inbound_emails WHERE outcome <> 'Failed' AND received_at < p_inbound_before;
                    GET DIAGNOSTICS v_inbound = ROW_COUNT;
                    DELETE FROM public.outbound_emails WHERE status <> 'Pending' AND created_at < p_outbound_before;
                    GET DIAGNOSTICS v_outbound = ROW_COUNT;
                    RETURN v_inbound + v_outbound;
                END;
                $$;
                """);
        }

        private static readonly string[] Tables = ["mailboxes", "inbound_emails", "outbound_emails", "chat_widgets", "chat_sessions", "chat_messages"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION zelvo_purge_mail_logs(timestamptz, timestamptz);
                DROP FUNCTION zelvo_idle_chat_tenants(timestamptz);
                DROP FUNCTION zelvo_mail_due_tenants();
                """);
            foreach (var table in Tables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }
            migrationBuilder.DropForeignKey(
                name: "fk_ticket_attachments_ticket_comments_comment_id",
                table: "ticket_attachments");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_mailboxes_mailbox_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "inbound_emails");

            migrationBuilder.DropTable(
                name: "outbound_emails");

            migrationBuilder.DropTable(
                name: "chat_sessions");

            migrationBuilder.DropTable(
                name: "mailboxes");

            migrationBuilder.DropTable(
                name: "chat_widgets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_mailbox_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_ticket_comments_tenant_id_email_message_id",
                table: "ticket_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_comments_channel_public",
                table: "ticket_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_comments_source",
                table: "ticket_comments");

            migrationBuilder.DropIndex(
                name: "ix_ticket_attachments_comment_id",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_attachments_comment",
                table: "ticket_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_support_groups_chat_limit",
                table: "support_groups");

            migrationBuilder.DropColumn(
                name: "mailbox_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "chat_session_id",
                table: "ticket_comments");

            migrationBuilder.DropColumn(
                name: "email_in_reply_to",
                table: "ticket_comments");

            migrationBuilder.DropColumn(
                name: "email_message_id",
                table: "ticket_comments");

            migrationBuilder.DropColumn(
                name: "mailbox_id",
                table: "ticket_comments");

            migrationBuilder.DropColumn(
                name: "source",
                table: "ticket_comments");

            migrationBuilder.DropColumn(
                name: "comment_id",
                table: "ticket_attachments");

            migrationBuilder.DropColumn(
                name: "chat_limit",
                table: "support_groups");

            migrationBuilder.DropColumn(
                name: "comment_id",
                table: "notifications");
        }
    }
}
