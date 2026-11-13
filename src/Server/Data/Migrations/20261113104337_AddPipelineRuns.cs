using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommandJson",
                table: "BuildRuns");

            migrationBuilder.AddColumn<string>(
                name: "GitBranch",
                table: "BuildRuns",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PipelinePath",
                table: "BuildRuns",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PipelineSpecJson",
                table: "BuildRuns",
                type: "text",
                nullable: true);

            // F-014 backfill: every pre-existing row was an argv run (a v0.2/v0.3 leftover). Those
            // runs stay terminal and are never re-executed, but the rows must stay readable - so each
            // one is given the branch and pipeline path of the project it belonged to (a snapshot,
            // exactly as a new trigger would take) and its frozen spec stays NULL, which is the honest
            // "this run never had a pipeline definition".
            //
            // One correlated subquery per column on purpose: it is what both providers understand
            // (PostgreSQL also allows UPDATE ... FROM, SQLite does not), and the migrations apply to
            // the SQLite development database as well as to PostgreSQL (docs/development.md).
            migrationBuilder.Sql(
                """
                UPDATE "BuildRuns" SET
                    "GitBranch" = COALESCE((SELECT "DefaultBranch" FROM "Projects" WHERE "Projects"."Id" = "BuildRuns"."ProjectId"), 'main'),
                    "PipelinePath" = COALESCE((SELECT "PipelinePath" FROM "Projects" WHERE "Projects"."Id" = "BuildRuns"."ProjectId"), 'pipeline.lua')
                WHERE "GitBranch" = '' OR "PipelinePath" = '';
                """);
        }

        /// <summary>
        /// Rolling back re-creates <c>CommandJson</c>, and <b>the argv cannot be recovered</b>: the column was
        /// dropped, so no row still knows the command it used to run. Each row therefore gets the
        /// single-element argv <c>["argv-not-recoverable"]</c>, whose element is a self-describing sentinel
        /// rather than a program name: an old worker that picks such a row up tries to start that
        /// (nonexistent) program and fails with a readable error, instead of pretending the run had no
        /// command. Why not the empty string: <c>""</c> decodes to an <em>empty</em> argv, which an old
        /// worker treats as "nothing to run" - a silent lie about a run that did execute something.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitBranch",
                table: "BuildRuns");

            migrationBuilder.DropColumn(
                name: "PipelinePath",
                table: "BuildRuns");

            migrationBuilder.DropColumn(
                name: "PipelineSpecJson",
                table: "BuildRuns");

            migrationBuilder.AddColumn<string>(
                name: "CommandJson",
                table: "BuildRuns",
                type: "text",
                nullable: false,
                defaultValue: "");

            // F-014: the argv is gone for good, so the rollback records a self-describing marker instead of
            // an empty value. The marker is deliberately a *valid* one-element argv (so it decodes to
            // exactly one, obviously bogus, program name that cannot be executed by accident) rather than
            // an empty string, which would decode to an empty argv and read as "this run had no command".
            migrationBuilder.Sql(
                """
                UPDATE "BuildRuns" SET "CommandJson" = '["argv-not-recoverable"]'
                WHERE "CommandJson" = '';
                """);
        }
    }
}
