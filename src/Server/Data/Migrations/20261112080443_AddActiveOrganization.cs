using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveOrganizationId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ActiveOrganizationId",
                table: "AspNetUsers",
                column: "ActiveOrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Organizations_ActiveOrganizationId",
                table: "AspNetUsers",
                column: "ActiveOrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Organizations_ActiveOrganizationId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ActiveOrganizationId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ActiveOrganizationId",
                table: "AspNetUsers");
        }
    }
}
