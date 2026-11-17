using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BranchDiscovery",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "single");

            migrationBuilder.CreateTable(
                name: "BranchJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PipelineState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObservedCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrphanedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OrphanedEvidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PendingCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PendingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObservationAttempts = table.Column<int>(type: "integer", nullable: false),
                    PendingTags = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchJobs", x => x.Id);
                    table.CheckConstraint("CK_BranchJobs_OrphanedEvidence", "(\"OrphanedAt\" IS NULL AND \"OrphanedEvidence\" IS NULL) OR (\"OrphanedAt\" IS NOT NULL AND \"OrphanedEvidence\" IS NOT NULL AND \"OrphanedEvidence\" IN ('push-delete', 'refs-absent'))");
                    table.CheckConstraint("CK_BranchJobs_PipelineState", "\"PipelineState\" IN ('unknown', 'has-pipeline', 'no-pipeline')");
                    table.ForeignKey(
                        name: "FK_BranchJobs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BranchJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_BranchDiscovery",
                table: "Projects",
                sql: "\"BranchDiscovery\" IN ('single', 'branches')");

            migrationBuilder.CreateIndex(
                name: "IX_BranchJobs_OrganizationId_ProjectId",
                table: "BranchJobs",
                columns: new[] { "OrganizationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchJobs_ProjectId_Branch",
                table: "BranchJobs",
                columns: new[] { "ProjectId", "Branch" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_BranchDiscovery",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BranchDiscovery",
                table: "Projects");
        }
    }
}
