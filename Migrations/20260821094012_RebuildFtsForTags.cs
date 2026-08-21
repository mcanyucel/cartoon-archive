using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CartoonArchive.Migrations
{
    /// <inheritdoc />
    public partial class RebuildFtsForTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_au_ins;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_au_del;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS CartoonsFts;");

            migrationBuilder.Sql(
                """
                CREATE VIRTUAL TABLE CartoonsFts USING fts5(
                    Description,
                    TagsText,
                    content='Cartoons',
                    content_rowid='Id',
                    tokenize = "unicode61 remove_diacritics 2"
                );
                """
            );

            migrationBuilder.Sql(
                """
                INSERT INTO CartoonsFts(rowid, Description, TagsText)
                SELECT Id, Description, TagsText FROM Cartoons;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_ai AFTER INSERT ON Cartoons BEGIN
                    INSERT INTO CartoonsFts(rowid, Description, TagsText)
                    VALUES (new.Id, new.Description, new.TagsText);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_ad AFTER DELETE ON Cartoons BEGIN
                    INSERT INTO CartoonsFts(CartoonsFts, rowid, Description, TagsText)
                    VALUES ('delete', old.Id, old.Description, old.TagsText);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_au AFTER UPDATE OF Description, TagsText ON Cartoons BEGIN
                    INSERT INTO CartoonsFts(CartoonsFts, rowid, Description, TagsText)
                    VALUES ('delete', old.Id, old.Description, old.TagsText);
                    INSERT INTO CartoonsFts(rowid, Description, TagsText)
                    VALUES (new.Id, new.Description, new.TagsText);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER CartoonTags_ai AFTER INSERT ON CartoonTags BEGIN
                    UPDATE Cartoons
                    SET TagsText = COALESCE((
                        SELECT GROUP_CONCAT(t.Name, ' ')
                        FROM CartoonTags ct
                        JOIN Tags t ON t.Id = ct.TagId
                        WHERE ct.CartoonId = NEW.CartoonId
                    ), '')
                    WHERE Id = NEW.CartoonId;
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER CartoonTags_ad AFTER DELETE ON CartoonTags BEGIN
                    UPDATE Cartoons
                    SET TagsText = COALESCE((
                        SELECT GROUP_CONCAT(t.Name, ' ')
                        FROM CartoonTags ct
                        JOIN Tags t ON t.Id = ct.TagId
                        WHERE ct.CartoonId = OLD.CartoonId
                    ), '')
                    WHERE Id = OLD.CartoonId;
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Tags_au AFTER UPDATE OF Name ON Tags BEGIN
                    UPDATE Cartoons
                    SET TagsText = COALESCE((
                        SELECT GROUP_CONCAT(t.Name, ' ')
                        FROM CartoonTags ct
                        JOIN Tags t ON t.Id = ct.TagId
                        WHERE ct.CartoonId = Cartoons.Id
                    ), '')
                    WHERE Id IN (SELECT CartoonId FROM CartoonTags WHERE TagId = NEW.Id);
                END;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Tags_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS CartoonTags_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS CartoonTags_ai;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Cartoons_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS CartoonsFts;");

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
                INSERT INTO CartoonsFts(rowid, Description)
                SELECT Id, Description FROM Cartoons WHERE Description IS NOT NULL;
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
                CREATE TRIGGER Cartoons_au_del AFTER UPDATE OF Description ON C
                WHEN old.Description IS NOT NULL BEGIN
                    INSERT INTO CartoonsFts(CartoonsFts, rowid, Description)
                    VALUES ('delete', old.Id, old.Description);
                END;
                """
            );

            migrationBuilder.Sql(
                """
                CREATE TRIGGER Cartoons_au_ins AFTER UPDATE OF Description ON C
                WHEN new.Description IS NOT NULL BEGIN
                    INSERT INTO CartoonsFts(rowid, Description)
                    VALUES (new.Id, new.Description);
                END;
                """
            );
        }
    }
}
