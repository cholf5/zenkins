using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildNumbersAndRebuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BuildRuns_ProjectId",
                table: "BuildRuns");

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "BuildRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RebuiltFromRunId",
                table: "BuildRuns",
                type: "uuid",
                nullable: true);

            // Card F-036: backfill every existing run's number, per project, in that project's own creation
            // order (CreatedAt, then Id). Both keys are part of a total order - Id is unique - so the
            // statement is deterministic: the same rows always get the same numbers, on either provider.
            //
            // It cannot produce a duplicate either: a row's rank is "how many rows of this project come
            // strictly before it", so the ranks inside one project are exactly 0..n-1 and the "+ 1" makes
            // them 1..n - a bijection, written as a portable correlated count instead of a window function
            // so PostgreSQL and SQLite answer it identically (the schema must stay provider-neutral).
            //
            // The column's migration default of 0 is unreachable through the product (every insert assigns a
            // number under the project row lock) and is removed by the next migration
            // (`AddBuildNumberPositiveCheck`, which also makes 0 impossible). Until then its only effect is
            // that an out-of-band insert would collide on the unique index below rather than silently share #0.
            //
            // One premise worth stating: on SQLite `CreatedAt` is stored as TEXT, so the comparison above is
            // lexical. Every row the product writes is UTC with the same offset spelling, which makes the
            // lexical order the chronological one; a hand-written row with a different offset could sort by
            // wall-clock instead. The backfill stays deterministic either way (it is a total order over the
            // stored values) - it is the *meaning* of "creation order" that assumes the uniform spelling.
            migrationBuilder.Sql(
                """
                UPDATE "BuildRuns"
                SET "Number" = (
                    SELECT COUNT(*) + 1
                    FROM "BuildRuns" AS "earlier"
                    WHERE "earlier"."ProjectId" = "BuildRuns"."ProjectId"
                      AND ("earlier"."CreatedAt" < "BuildRuns"."CreatedAt"
                        OR ("earlier"."CreatedAt" = "BuildRuns"."CreatedAt"
                            AND "earlier"."Id" < "BuildRuns"."Id"))
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BuildRuns_ProjectId_Number",
                table: "BuildRuns",
                columns: new[] { "ProjectId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BuildRuns_ProjectId_Number",
                table: "BuildRuns");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "BuildRuns");

            migrationBuilder.DropColumn(
                name: "RebuiltFromRunId",
                table: "BuildRuns");

            migrationBuilder.CreateIndex(
                name: "IX_BuildRuns_ProjectId",
                table: "BuildRuns",
                column: "ProjectId");
        }
    }
}
