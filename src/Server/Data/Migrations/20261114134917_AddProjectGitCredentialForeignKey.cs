using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectGitCredentialForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Projects_GitCredentialId",
                table: "Projects",
                column: "GitCredentialId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_GitCredentials_GitCredentialId",
                table: "Projects",
                column: "GitCredentialId",
                principalTable: "GitCredentials",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_GitCredentials_GitCredentialId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_GitCredentialId",
                table: "Projects");
        }
    }
}
