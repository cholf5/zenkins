using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionKeep",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "BuildRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Native DROP COLUMN, not migrationBuilder.DropColumn: on SQLite that call rebuilds the whole table, and the
            // rebuild recreates the indexes this migration's generated script knew about. Card F-042 added its folder
            // indexes on "Projects" in a parallel branch, so this migration's stale index set silently dropped them
            // while a downgrade passed through - and the F-042 migration's own Down then failed with
            // "no such index: IX_Projects_FolderId". None of these three columns is indexed and no constraint
            // mentions them, so the native statement is legal on both providers and leaves the tables' indexes alone.
            migrationBuilder.Sql("ALTER TABLE \"Projects\" DROP COLUMN \"RetentionDays\";");
            migrationBuilder.Sql("ALTER TABLE \"Projects\" DROP COLUMN \"RetentionKeep\";");
            migrationBuilder.Sql("ALTER TABLE \"BuildRuns\" DROP COLUMN \"Pinned\";");
        }
    }
}
