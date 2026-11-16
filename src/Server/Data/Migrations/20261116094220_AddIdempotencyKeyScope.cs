using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zenkins.Server.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Card B-008: the idempotency record gains the project, a payload hash and an expiry.
    /// <para>
    /// <b>Existing rows are deliberately backfilled as expired.</b> A record written before this migration
    /// has no project and no payload hash, so it cannot safely describe "the request" a key names: the
    /// defaults (<c>ExpiresAt</c> at DateTimeOffset.MinValue, an empty project and hash) make the lookup
    /// treat it as expired and forget it. That is the only honest answer - a key whose scope is unknown
    /// must not replay anything.
    /// </para>
    /// </summary>
    public partial class AddIdempotencyKeyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "ApiIdempotencyRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "ApiIdempotencyRecords",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "ApiIdempotencyRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ApiIdempotencyRecords_ExpiresAt",
                table: "ApiIdempotencyRecords",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiIdempotencyRecords_ExpiresAt",
                table: "ApiIdempotencyRecords");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "ApiIdempotencyRecords");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "ApiIdempotencyRecords");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "ApiIdempotencyRecords");
        }
    }
}
