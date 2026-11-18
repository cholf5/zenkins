using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletionNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "LinkId",
                table: "OutboxMessages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "NotificationEndpointId",
                table: "OutboxMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationEvent",
                table: "OutboxMessages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationSecretCiphertext",
                table: "OutboxMessages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AllowInsecureHttp = table.Column<bool>(type: "boolean", nullable: false),
                    SecretCiphertext = table.Column<string>(type: "text", nullable: false),
                    SecretRotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Events = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastDeliveryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationEndpoints_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Kind_Status",
                table: "OutboxMessages",
                columns: new[] { "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_NotificationEndpointId",
                table: "OutboxMessages",
                column: "NotificationEndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEndpoints_ProjectId",
                table: "NotificationEndpoints",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_OutboxMessages_NotificationEndpoints_NotificationEndpointId",
                table: "OutboxMessages",
                column: "NotificationEndpointId",
                principalTable: "NotificationEndpoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OutboxMessages_NotificationEndpoints_NotificationEndpointId",
                table: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "NotificationEndpoints");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Kind_Status",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_NotificationEndpointId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "NotificationEndpointId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "NotificationEvent",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "NotificationSecretCiphertext",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<Guid>(
                name: "LinkId",
                table: "OutboxMessages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
