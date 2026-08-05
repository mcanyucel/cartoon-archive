## FTS5 Virtual Tables for Full-Text Search

The virtual table 

```sql
CREATE VIRTUAL TABLE CartoonsFts USING fts5(
    Description,
    content='Cartoons',
    content_rowid='Id',
    tokenize= "unicode61 remove_diacritics 2"
);
```

has the below features:

* `content='Cartoons'`, `content_rowid='Id'`: "external content" mode. FTS5 stores only the search index (word -> rowid postings); the actual `Description` text lives in the `Cartoons` table. Two upsides: no duplicated text, and we can `SELECT Description FROM CartoonsFts WHERE CartoonsFts MATCH 'foo'` and FTS pulls the text back from the `Cartoons` automatically.
* `tokenize = "unicode61 remove_diacritics 2`: Unicode-aware tokenizer, case-folds, and (with `remove_diacritics 2`) strips diacritics including Turkish `ı, i, İ` and German umlauts. So "yalniz" matches "yanlız", "uber" matches "über", etc. 

### The Four Triggers

They keep the FTS index in sync with `Cartoons.Description`:

| Trigger | When | Does |
| --      |  --  | --   |
| Cartoons_ai | AFTER INSERT, if new description is non-null | index the new row | 
| Cartoons_ad | AFTER DELETE, if old description was non-null | remove from the index |
| Cartoons_au_del | AFTER UPDATE of Description, if old was non-null | Remove old tokens | 
| Cartoons_au_ins | AFTER UPDATE of Description, if new is non-null | Add new tokens |

* Split into 4 rather than combined: Each does one thing, easier to reason about. The `WHEN` guards mean they no-op when `Description` is null (i.e. while a row is still `Pending`).
* `AFTER UPDATE of Description`: The trigger only fires when `Description` is in the UPDATE statement's column list. EF Core by default only updates changed columns, so this keeps unrelated updates (e.g. `Status = Ready` without changing `Description` in the same statement) from touching the FTS index.
* The `delete` command syntax: That weird `INSERT INTO CartoonsFts(CartoonsFts, rowid, Description) VALUES ('delete', ... )` is FTS5's special-command form for external content tables. It tells FTS "remove postings for this rowid, using this old text to know which tokens to remove".

### The `Down` Migration

* Drops triggers first, then the FTS table. 
* Won't ever run in production but keeps EF's rollback story clean. 
