using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScmPolling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPollAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPollMessage",
                table: "Projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPollOutcome",
                table: "Projects",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PollFailureStreak",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalMinutes",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PollingEnabled",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BranchHeads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    LastOutcomeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchHeads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchHeads_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BranchHeads_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChangeTriggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TriggerType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeTriggers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeTriggers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChangeTriggers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchHeads_OrganizationId",
                table: "BranchHeads",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchHeads_ProjectId_Branch",
                table: "BranchHeads",
                columns: new[] { "ProjectId", "Branch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeTriggers_OrganizationId",
                table: "ChangeTriggers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeTriggers_ProjectId_Branch_Commit",
                table: "ChangeTriggers",
                columns: new[] { "ProjectId", "Branch", "Commit" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchHeads");

            migrationBuilder.DropTable(
                name: "ChangeTriggers");

            migrationBuilder.DropColumn(
                name: "LastPollAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastPollMessage",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastPollOutcome",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PollFailureStreak",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PollIntervalMinutes",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PollingEnabled",
                table: "Projects");
        }
    }
}
