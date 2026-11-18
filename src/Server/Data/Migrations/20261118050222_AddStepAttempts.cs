using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Card F-032: one attempt count per recorded step.
    /// <para>
    /// <b>Effect on rows that already exist.</b> A new non-nullable <c>BuildSteps.Attempts</c> column with a
    /// database default of 1: every step recorded before this card made exactly one attempt, so 1 is the
    /// honest backfill rather than a placeholder. No rewrite, no separate data migration - and the default
    /// matters, because the check constraint added beside it (<c>1..5</c>, the schema's own retry cap) would
    /// refuse the zero a default-less column would leave behind on every existing row.
    /// </para>
    /// <para>
    /// One row per declared step is the point of the column: the attempts of a retried step live in that
    /// step's log as delimited sections, so "step N of the frozen spec" keeps the identity artifacts,
    /// conditions and the run page's merge logic index by.
    /// </para>
    /// </summary>
    public partial class AddStepAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "BuildSteps",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BuildSteps_Attempts",
                table: "BuildSteps",
                sql: "\"Attempts\" BETWEEN 1 AND 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BuildSteps_Attempts",
                table: "BuildSteps");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "BuildSteps");
        }
    }
}
