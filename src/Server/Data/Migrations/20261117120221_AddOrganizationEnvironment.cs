using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Card F-034: the organization environment layer.
    /// <para>
    /// <b>Effect on rows that already exist.</b> Two additions, both additive and both safe on a
    /// populated database:
    /// </para>
    /// <list type="number">
    /// <item><b><c>OrganizationEnvironmentVariables</c> is a new table</b>, so it starts with zero rows in
    /// every existing installation: no backfill, no rewrite, and nothing to migrate <em>into</em> it (the
    /// feature's data is authored by an Owner after the upgrade).</item>
    /// <item><b><c>BuildRuns.EnvironmentJson</c> is a new nullable column</b> on a table that has rows. It
    /// starts <see langword="null"/> on every existing run, which is exactly the meaning the product
    /// gives it: "this run was queued before the organization layer existed", so its steps receive no
    /// organization variables and the run page shows no organization environment card. No default, no
    /// backfill and no rewrite is needed - and null is never confused with an empty layer, because
    /// <c>RunEnvironmentSnapshot.Serialize</c> writes null for an empty set.</item>
    /// </list>
    /// </summary>
    public partial class AddOrganizationEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentJson",
                table: "BuildRuns",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrganizationEnvironmentVariables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationEnvironmentVariables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationEnvironmentVariables_Organizations_Organization~",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationEnvironmentVariables_OrganizationId_Name",
                table: "OrganizationEnvironmentVariables",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationEnvironmentVariables");

            migrationBuilder.DropColumn(
                name: "EnvironmentJson",
                table: "BuildRuns");
        }
    }
}
