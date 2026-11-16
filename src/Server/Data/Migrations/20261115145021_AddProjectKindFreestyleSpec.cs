using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectKindFreestyleSpec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RepositoryUrl",
                table: "Projects",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "PipelinePath",
                table: "Projects",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AlterColumn<string>(
                name: "DefaultBranch",
                table: "Projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "FreestyleSpecJson",
                table: "Projects",
                type: "text",
                nullable: true);

            // F-020: every project that predates the kind column is a Pipeline project - the
            // backfill value is what the check constraint (and the product) expects. A defaultValue
            // of "" would insert rows the constraint refuses.
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "pipeline");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_Kind",
                table: "Projects",
                sql: "\"Kind\" IN ('pipeline', 'freestyle')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_Kind",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FreestyleSpecJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Projects");

            migrationBuilder.AlterColumn<string>(
                name: "RepositoryUrl",
                table: "Projects",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PipelinePath",
                table: "Projects",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DefaultBranch",
                table: "Projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
