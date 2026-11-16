using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectEnableAndQueueControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Disabled",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledByUserId",
                table: "Projects",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledReason",
                table: "Projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentRuns",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxQueuedRuns",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuietPeriodSeconds",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_MaxConcurrentRuns",
                table: "Projects",
                sql: "\"MaxConcurrentRuns\" IS NULL OR \"MaxConcurrentRuns\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_MaxQueuedRuns",
                table: "Projects",
                sql: "\"MaxQueuedRuns\" IS NULL OR \"MaxQueuedRuns\" >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_QuietPeriodSeconds",
                table: "Projects",
                sql: "\"QuietPeriodSeconds\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_MaxConcurrentRuns",
                table: "Projects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_MaxQueuedRuns",
                table: "Projects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_QuietPeriodSeconds",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Disabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DisabledAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DisabledByUserId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DisabledReason",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentRuns",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "MaxQueuedRuns",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "QuietPeriodSeconds",
                table: "Projects");
        }
    }
}
