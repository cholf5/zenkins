using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitProbeCapability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanProbe",
                table: "Workers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastProbeAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastProbeMessage",
                table: "Projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastProbeOutcome",
                table: "Projects",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_LastProbeOutcome",
                table: "Projects",
                sql: "\"LastProbeOutcome\" IS NULL OR \"LastProbeOutcome\" IN ('refs', 'git-auth-rejected', 'git-unreachable', 'git-timeout', 'git-failed', 'no-worker')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_LastProbeOutcome",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CanProbe",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastProbeAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastProbeMessage",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LastProbeOutcome",
                table: "Projects");
        }
    }
}
