using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CartoonArchive.Migrations
{
    /// <inheritdoc />
    public partial class AddShareTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShareTokens",
                columns: table => new
                {
                    Token = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CartoonId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareTokens", x => x.Token);
                    table.ForeignKey(
                        name: "FK_ShareTokens_Cartoons_CartoonId",
                        column: x => x.CartoonId,
                        principalTable: "Cartoons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShareTokens_CartoonId",
                table: "ShareTokens",
                column: "CartoonId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareTokens_ExpiresAt",
                table: "ShareTokens",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShareTokens");
        }
    }
}
