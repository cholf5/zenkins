using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectPostFailureSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PostFailureFailsRun",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Native DROP COLUMN, not migrationBuilder.DropColumn: on SQLite that call rebuilds the whole "Projects"
            // table, and the rebuild recreates the indexes this migration's generated script knew about - which,
            // because card F-042 added its folder indexes in a parallel branch, are the OLD ones. Downgrading past
            // this migration therefore dropped IX_Projects_FolderId and IX_Projects_OrganizationId_FolderKey_NameKey
            // early, and the F-042 migration's own Down then failed with "no such index: IX_Projects_FolderId"
            // (found when two branches met on main). The column is not indexed and no constraint mentions it, so the
            // native statement is legal on both providers and leaves the rest of the table - and its indexes - alone.
            migrationBuilder.Sql("ALTER TABLE \"Projects\" DROP COLUMN \"PostFailureFailsRun\";");
        }
    }
}
