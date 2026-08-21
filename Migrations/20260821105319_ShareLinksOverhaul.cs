using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CartoonArchive.Migrations
{
    /// <inheritdoc />
    public partial class ShareLinksOverhaul : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShareTokens_ExpiresAt",
                table: "ShareTokens");

            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                table: "ShareTokens",
                newName: "ActiveUntil");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MetadataUntil",
                table: "ShareTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql(
                "UPDATE ShareTokens SET MetadataUntil = strftime('%Y-%m-%d %H:%M:%f+00:00', ActiveUntil, '+7 days');");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "ShareTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql(
                "UPDATE ShareTokens SET CreatedAt = strftime('%Y-%m-%d %H:%M:%f+00:00', ActiveUntil, '-1 hour');");

            migrationBuilder.CreateIndex(
                name: "IX_ShareTokens_MetadataUntil",
                table: "ShareTokens",
                column: "MetadataUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShareTokens_MetadataUntil",
                table: "ShareTokens");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ShareTokens");

            migrationBuilder.DropColumn(
                name: "MetadataUntil",
                table: "ShareTokens");

            migrationBuilder.RenameColumn(
                name: "ActiveUntil",
                table: "ShareTokens",
                newName: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ShareTokens_ExpiresAt",
                table: "ShareTokens",
                column: "ExpiresAt");
        }
    }
}
