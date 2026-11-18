using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunProtectedParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParameterDetailsJson",
                table: "BuildRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedParametersJson",
                table: "BuildRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParameterDetailsJson",
                table: "BuildRuns");

            migrationBuilder.DropColumn(
                name: "ProtectedParametersJson",
                table: "BuildRuns");
        }
    }
}
