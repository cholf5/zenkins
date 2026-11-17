using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectPipelineSourceAndInlineScript : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InlineScript",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PipelineSource",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "repository");

            migrationBuilder.AddColumn<string>(
                name: "PipelineScriptText",
                table: "BuildRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_PipelineSource",
                table: "Projects",
                sql: "\"PipelineSource\" IN ('repository', 'inline')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_PipelineSource",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "InlineScript",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PipelineSource",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PipelineScriptText",
                table: "BuildRuns");
        }
    }
}
