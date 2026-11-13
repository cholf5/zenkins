using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SecretCiphertext = table.Column<string>(type: "text", nullable: false),
                    SecretRotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BranchFilter = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RequiredTags = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    LastDeliveryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastDeliveryOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhooks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Webhooks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WebhookId = table.Column<Guid>(type: "uuid", nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_OrganizationId",
                table: "WebhookDeliveries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WebhookId_DedupeKey",
                table: "WebhookDeliveries",
                columns: new[] { "WebhookId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_OrganizationId_ProjectId",
                table: "Webhooks",
                columns: new[] { "OrganizationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_ProjectId_Name",
                table: "Webhooks",
                columns: new[] { "ProjectId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "Webhooks");
        }
    }
}
