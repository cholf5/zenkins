using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cron = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    QueueAnyway = table.Column<bool>(type: "boolean", nullable: false),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastMissedCount = table.Column<int>(type: "integer", nullable: false),
                    LastMissedWindowTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSchedules_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectSchedules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunNumber = table.Column<int>(type: "integer", nullable: true),
                    CollapsedDueTimes = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleOccurrences", x => x.Id);
                    table.CheckConstraint("CK_ScheduleOccurrences_CollapsedDueTimes", "\"CollapsedDueTimes\" >= 0");
                    table.ForeignKey(
                        name: "FK_ScheduleOccurrences_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduleOccurrences_ProjectSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "ProjectSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSchedules_OrganizationId_ProjectId",
                table: "ProjectSchedules",
                columns: new[] { "OrganizationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSchedules_ProjectId_Cron_TimeZone",
                table: "ProjectSchedules",
                columns: new[] { "ProjectId", "Cron", "TimeZone" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleOccurrences_OrganizationId_ScheduleId",
                table: "ScheduleOccurrences",
                columns: new[] { "OrganizationId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleOccurrences_ScheduleId_DueAt",
                table: "ScheduleOccurrences",
                columns: new[] { "ScheduleId", "DueAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleOccurrences");

            migrationBuilder.DropTable(
                name: "ProjectSchedules");
        }
    }
}
