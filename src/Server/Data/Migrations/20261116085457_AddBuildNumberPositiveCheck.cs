using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildNumberPositiveCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-036 (review M1): `AddBuildNumberPositiveCheck` is also where the previous migration's leftover
            // database default disappears. `AddColumn<int>(nullable: false)` has to supply a default for the
            // rows that already exist, so `20261116075749_AddBuildNumbersAndRebuild` left `DEFAULT 0` on the
            // column - and the backfill only fixed the rows that existed *then*. Without this, the first
            // out-of-band insert of a project would silently get `#0` (a renderable `/runs/0`, a `Run #0`,
            // a `ZENKINS_BUILD_NUMBER=0`), and only the second would collide. `oldDefaultValue` is what makes
            // the generator emit the DROP: on PostgreSQL an `ALTER COLUMN … DROP DEFAULT`, on SQLite the table
            // rebuild EF already performs for a column change (which is also how the check constraint below is
            // applied there).
            migrationBuilder.AlterColumn<int>(
                name: "Number",
                table: "BuildRuns",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            // ...and the number is positive, so 0 can never come back through a caller that skipped the
            // assignment (the product path always assigns `MAX + 1`, which is >= 1 by induction).
            migrationBuilder.AddCheckConstraint(
                name: "CK_BuildRuns_Number",
                table: "BuildRuns",
                sql: "\"Number\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BuildRuns_Number",
                table: "BuildRuns");

            // Back to the shape the previous migration left behind, so a downgrade is symmetric.
            migrationBuilder.AlterColumn<int>(
                name: "Number",
                table: "BuildRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");
        }
    }
}
