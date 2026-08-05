using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CartoonArchive.Migrations
{
    /// <inheritdoc />
    public partial class AddFulltextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE VIRTUAL TABLE CartoonsFts USING fts5(
                    Description,
                    content='Cartoons',
                    content_rowid='Id',
                    tokenize = "unicode61 remove_diacritics 2"
                );
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_ai AFTER INSERT ON Cartoons
                WHEN new.Description IS NOT NULL BEGIN
                    INSERT INTO CartoonsFts(rowid, Description)
                    VALUES (new.Id, new.Description);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_ad AFTER DELETE ON Cartoons
                WHEN old.Description IS NOT NULL BEGIN
                    INSERT INTO CartoonsFts(CartoonsFts, rowid, Description)
                    VALUES ('delete', old.Id, old.Description);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_au_del AFTER UPDATE OF Description ON Cartoons
                WHEN old.Description IS NOT NULL BEGIN
                    INSERT INTO CartoonsFts(CartoonsFts, rowid, Description)
                    VALUES ('delete', old.Id, old.Description);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_au_ins AFTER UPDATE OF Description ON Cartoons
                WHEN new.Description IS NOT NULL BEGIN
                    INSERT INTO CartoonsFts(rowid, Description)
                    VALUES (new.Id, new.Description);
                END;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_au_ins;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_au_del;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS CartoonsFts;");
        }
    }
}
