using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_OrganizationId_Name",
                table: "Projects");

            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FolderKey",
                table: "Projects",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "NameKey",
                table: "Projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // F-042: backfill the case-insensitive name key from the names that already exist, so the
            // folder-unique index below is satisfiable by every row this migration inherits. The old
            // (OrganizationId, Name) index was strictly stronger for identical names, so no two existing
            // projects of one organization can share a name - they land in the root (FolderKey =
            // Guid.Empty) with distinct keys. lower() is the one spelling both providers share, and it
            // matches FolderValidation.NameKeyFor (invariant lowercase). Nothing is renamed: a data set
            // that somehow holds case-only twins would fail the index creation instead of being silently
            // disambiguated.
            migrationBuilder.Sql("UPDATE \"Projects\" SET \"NameKey\" = lower(\"Name\");");

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.CheckConstraint("CK_Folders_Depth", "\"Depth\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Folders_ParentKey", "(\"ParentFolderId\" IS NULL AND \"ParentKey\" = '00000000-0000-0000-0000-000000000000') OR (\"ParentFolderId\" IS NOT NULL AND \"ParentKey\" <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_Folders_Folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Folders_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_FolderId",
                table: "Projects",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OrganizationId_FolderKey_NameKey",
                table: "Projects",
                columns: new[] { "OrganizationId", "FolderKey", "NameKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_FolderKey",
                table: "Projects",
                sql: "\"FolderId\" IS NULL AND \"FolderKey\" = '00000000-0000-0000-0000-000000000000' OR \"FolderId\" IS NOT NULL AND \"FolderKey\" <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OrganizationId_ParentKey_NameKey",
                table: "Folders",
                columns: new[] { "OrganizationId", "ParentKey", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Folders_FolderId",
                table: "Projects",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Folders_FolderId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Projects_FolderId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_OrganizationId_FolderKey_NameKey",
                table: "Projects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_FolderKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FolderKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "NameKey",
                table: "Projects");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OrganizationId_Name",
                table: "Projects",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);
        }
    }
}
