using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zelvo.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerVoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.CreateTable(
                name: "customer_voice_records",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: true),
                    root_cause_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    confirmation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    confirmation_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmation_comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    review = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    review_feedback = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    converted_to_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymous = table.Column<bool>(type: "boolean", nullable: false),
                    tracking_code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_voice_records", x => x.ticket_id);
                    table.CheckConstraint("ck_customer_voice_records_confirmation", "confirmation IN ('Pending', 'Confirmed', 'Rejected', 'NotConfirmed')");
                    table.CheckConstraint("ck_customer_voice_records_priority", "priority BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_customer_voice_records_review", "review IN ('UnderReview', 'Accepted', 'Implemented', 'Declined')");
                    table.CheckConstraint("ck_customer_voice_records_severity", "severity IS NULL OR severity BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_customer_voice_records_tracking", "anonymous = (tracking_code_hash IS NOT NULL)");
                    table.CheckConstraint("ck_customer_voice_records_tracking_hash", "tracking_code_hash IS NULL OR tracking_code_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_customer_voice_records_catalog_services_service_id",
                        column: x => x.service_id,
                        principalTable: "catalog_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_customer_voice_records_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_voice_records_tickets_converted_to_id",
                        column: x => x.converted_to_id,
                        principalTable: "tickets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_customer_voice_records_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "happiness_ratings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    score = table.Column<int>(type: "integer", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_happiness_ratings", x => x.id);
                    table.CheckConstraint("ck_happiness_ratings_channel", "channel IN ('Portal', 'Email', 'Chat', 'WhatsApp', 'Phone', 'Api', 'AiAgent')");
                    table.CheckConstraint("ck_happiness_ratings_device_hash", "device_hash IS NULL OR device_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_happiness_ratings_points", "points IN (3, 5)");
                    table.CheckConstraint("ck_happiness_ratings_record_type", "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");
                    table.CheckConstraint("ck_happiness_ratings_score", "score BETWEEN 1 AND points");
                    table.ForeignKey(
                        name: "fk_happiness_ratings_catalog_services_service_id",
                        column: x => x.service_id,
                        principalTable: "catalog_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_happiness_ratings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_happiness_ratings_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_happiness_ratings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest', 'Complaint', 'Inquiry', 'Suggestion')");

            migrationBuilder.CreateIndex(
                name: "ix_customer_voice_records_converted_to_id",
                table: "customer_voice_records",
                column: "converted_to_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_voice_records_service_id",
                table: "customer_voice_records",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_voice_records_tenant_id_service_id",
                table: "customer_voice_records",
                columns: new[] { "tenant_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_happiness_ratings_service_id",
                table: "happiness_ratings",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_happiness_ratings_tenant_id_created_at",
                table: "happiness_ratings",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_happiness_ratings_ticket_id",
                table: "happiness_ratings",
                column: "ticket_id",
                unique: true,
                filter: "ticket_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_happiness_ratings_user_id",
                table: "happiness_ratings",
                column: "user_id");

            foreach (var table in new[] { "customer_voice_records", "happiness_ratings" })
            {
                migrationBuilder.EnableTenantIsolation(table);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "customer_voice_records", "happiness_ratings" })
            {
                migrationBuilder.DisableTenantIsolation(table);
            }

            migrationBuilder.DropTable(
                name: "customer_voice_records");

            migrationBuilder.DropTable(
                name: "happiness_ratings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_type",
                table: "tickets",
                sql: "type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_number_sequences_type",
                table: "ticket_number_sequences",
                sql: "type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_policies_record_type",
                table: "sla_policies",
                sql: "record_type IN ('Incident', 'ServiceRequest')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_bindings_record_type",
                table: "form_bindings",
                sql: "record_type IN ('Incident', 'ServiceRequest')");
        }
    }
}
