using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class Sla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "responded_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "calendar_id",
                table: "support_groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "business_calendars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_calendars", x => x.id);
                    table.ForeignKey(
                        name: "fk_business_calendars_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    email_domains = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_customer_groups_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "business_calendar_holidays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_calendar_holidays", x => x.id);
                    table.CheckConstraint("ck_business_calendar_holidays_dates", "start_date <= end_date");
                    table.ForeignKey(
                        name: "fk_business_calendar_holidays_business_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "business_calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "business_calendar_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    start_minute = table.Column<int>(type: "integer", nullable: false),
                    end_minute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_calendar_periods", x => x.id);
                    table.CheckConstraint("ck_business_calendar_periods_day", "day BETWEEN 0 AND 6");
                    table.CheckConstraint("ck_business_calendar_periods_minutes", "start_minute >= 0 AND end_minute <= 1440 AND start_minute < end_minute");
                    table.ForeignKey(
                        name: "fk_business_calendar_periods_business_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "business_calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_group_members",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_group_members", x => new { x.group_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_customer_group_members_customer_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "customer_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_group_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sla_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    record_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    name_en = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_policies", x => x.id);
                    table.CheckConstraint("ck_sla_policies_record_type", "record_type IN ('Incident')");
                    table.ForeignKey(
                        name: "fk_sla_policies_business_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "business_calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sla_policies_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sla_policies_customer_groups_customer_group_id",
                        column: x => x.customer_group_id,
                        principalTable: "customer_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sla_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sla_escalations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    percent = table.Column<int>(type: "integer", nullable: false),
                    notify_assignee = table.Column<bool>(type: "boolean", nullable: false),
                    notify_group_leads = table.Column<bool>(type: "boolean", nullable: false),
                    notify_group_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_escalations", x => x.id);
                    table.CheckConstraint("ck_sla_escalations_metric", "metric IN ('Response', 'Resolution')");
                    table.CheckConstraint("ck_sla_escalations_percent", "percent BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "fk_sla_escalations_sla_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "sla_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sla_escalations_support_groups_notify_group_id",
                        column: x => x.notify_group_id,
                        principalTable: "support_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sla_policy_targets",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    response_minutes = table.Column<int>(type: "integer", nullable: true),
                    resolution_minutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_policy_targets", x => new { x.policy_id, x.priority });
                    table.CheckConstraint("ck_sla_policy_targets_minutes", "(response_minutes IS NULL OR response_minutes BETWEEN 1 AND 525600) AND (resolution_minutes IS NULL OR resolution_minutes BETWEEN 1 AND 525600)");
                    table.CheckConstraint("ck_sla_policy_targets_priority", "priority BETWEEN 1 AND 9");
                    table.ForeignKey(
                        name: "fk_sla_policy_targets_sla_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "sla_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_slas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_minutes = table.Column<int>(type: "integer", nullable: true),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    elapsed_seconds = table.Column<long>(type: "bigint", nullable: false),
                    counted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reached_percent = table.Column<int>(type: "integer", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_slas", x => x.id);
                    table.CheckConstraint("ck_ticket_slas_elapsed", "elapsed_seconds >= 0");
                    table.CheckConstraint("ck_ticket_slas_metric", "metric IN ('Response', 'Resolution')");
                    table.CheckConstraint("ck_ticket_slas_state", "state IN ('Running', 'Paused', 'Stopped')");
                    table.ForeignKey(
                        name: "fk_ticket_slas_business_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalTable: "business_calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ticket_slas_sla_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "sla_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ticket_slas_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sla_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_sla_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percent = table.Column<int>(type: "integer", nullable: false),
                    reached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recipient_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_alerts", x => x.id);
                    table.CheckConstraint("ck_sla_alerts_metric", "metric IN ('Response', 'Resolution')");
                    table.CheckConstraint("ck_sla_alerts_percent", "percent BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "fk_sla_alerts_ticket_slas_ticket_sla_id",
                        column: x => x.ticket_sla_id,
                        principalTable: "ticket_slas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sla_alerts_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_support_groups_calendar_id",
                table: "support_groups",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_calendar_holidays_calendar_id",
                table: "business_calendar_holidays",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_calendar_holidays_tenant_id_calendar_id_start_date",
                table: "business_calendar_holidays",
                columns: new[] { "tenant_id", "calendar_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_business_calendar_periods_calendar_id",
                table: "business_calendar_periods",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_calendar_periods_tenant_id_calendar_id",
                table: "business_calendar_periods",
                columns: new[] { "tenant_id", "calendar_id" });

            migrationBuilder.CreateIndex(
                name: "ix_business_calendars_tenant_id",
                table: "business_calendars",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_calendars_tenant_id1",
                table: "business_calendars",
                column: "tenant_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_customer_group_members_tenant_id_user_id",
                table: "customer_group_members",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_group_members_user_id",
                table: "customer_group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_groups_tenant_id",
                table: "customer_groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_alerts_tenant_id_reached_at",
                table: "sla_alerts",
                columns: new[] { "tenant_id", "reached_at" },
                filter: "delivered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sla_alerts_tenant_id_ticket_id",
                table: "sla_alerts",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_alerts_ticket_id",
                table: "sla_alerts",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_alerts_ticket_sla_id_percent",
                table: "sla_alerts",
                columns: new[] { "ticket_sla_id", "percent" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_escalations_notify_group_id",
                table: "sla_escalations",
                column: "notify_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_escalations_policy_id",
                table: "sla_escalations",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_escalations_tenant_id_policy_id",
                table: "sla_escalations",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_calendar_id",
                table: "sla_policies",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_category_id",
                table: "sla_policies",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_customer_group_id",
                table: "sla_policies",
                column: "customer_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_tenant_id_position",
                table: "sla_policies",
                columns: new[] { "tenant_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_policy_targets_tenant_id",
                table: "sla_policy_targets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_calendar_id",
                table: "ticket_slas",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_policy_id",
                table: "ticket_slas",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_tenant_id_next_check_at",
                table: "ticket_slas",
                columns: new[] { "tenant_id", "next_check_at" },
                filter: "next_check_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_tenant_id_ticket_id",
                table: "ticket_slas",
                columns: new[] { "tenant_id", "ticket_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_ticket_id_metric",
                table: "ticket_slas",
                columns: new[] { "ticket_id", "metric" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_support_groups_business_calendars_calendar_id",
                table: "support_groups",
                column: "calendar_id",
                principalTable: "business_calendars",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            foreach (var table in new[] { "business_calendars", "business_calendar_periods", "business_calendar_holidays", "customer_groups", "customer_group_members", "sla_policies", "sla_policy_targets", "sla_escalations", "ticket_slas", "sla_alerts" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Tickets worked on before B3 count as responded to; their clocks start at their next change.
            migrationBuilder.Sql("""
                UPDATE tickets SET responded_at = COALESCE(resolved_at, updated_at)
                WHERE state NOT IN ('New', 'Assigned');
                """);

            // The SLA timer job finds tenants with clocks to look at; only ids and slugs come back.
            migrationBuilder.Sql("""
                CREATE FUNCTION zelvo_tenants_with_due_slas(due timestamptz)
                RETURNS TABLE (id uuid, slug text)
                LANGUAGE sql STABLE SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $$
                    SELECT t.id, t.slug::text
                    FROM public.tenants t
                    WHERE t.status = 'Active'
                      AND EXISTS (SELECT 1 FROM public.ticket_slas s WHERE s.tenant_id = t.id AND s.next_check_at <= due)
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION zelvo_tenants_with_due_slas(timestamptz);");
            foreach (var table in new[] { "business_calendars", "business_calendar_periods", "business_calendar_holidays", "customer_groups", "customer_group_members", "sla_policies", "sla_policy_targets", "sla_escalations", "ticket_slas", "sla_alerts" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropForeignKey(
                name: "fk_support_groups_business_calendars_calendar_id",
                table: "support_groups");

            migrationBuilder.DropTable(
                name: "business_calendar_holidays");

            migrationBuilder.DropTable(
                name: "business_calendar_periods");

            migrationBuilder.DropTable(
                name: "customer_group_members");

            migrationBuilder.DropTable(
                name: "sla_alerts");

            migrationBuilder.DropTable(
                name: "sla_escalations");

            migrationBuilder.DropTable(
                name: "sla_policy_targets");

            migrationBuilder.DropTable(
                name: "ticket_slas");

            migrationBuilder.DropTable(
                name: "sla_policies");

            migrationBuilder.DropTable(
                name: "business_calendars");

            migrationBuilder.DropTable(
                name: "customer_groups");

            migrationBuilder.DropIndex(
                name: "ix_support_groups_calendar_id",
                table: "support_groups");

            migrationBuilder.DropColumn(
                name: "responded_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "calendar_id",
                table: "support_groups");
        }
    }
}
