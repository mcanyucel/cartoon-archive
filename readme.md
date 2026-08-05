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

## `ImageStorage`

It writes bytes to a randomly-named temp file inside `data/images/` while hashing while hashing them in the same pass with `IncrementalHash`. When the write is done, we know the SHA256. It computes the final path as `{sha256}{ext}` - if a file with that name already exists (previous upload of the same bytes), it deletes the temp; otherwise `File.Move` the temp to the final name. It returns the hash. On any exception, it best-efforts t odelete the temp file. 

### Design Notes

* Single pass hash+write via `IncrementalHash`: The alternative is "hash the stream, then re-read to write" - requires a seekable stream (some request bodies aren't) or buffering the whole image in memory (bad for large files). One pass is both simpler and more scalable. 
* Temp file + rename (atomicity): `File.Move` on the same volume is atomic on both Windows and Linux; readers will never see a partially-written file. Random temp name avoids collisions if two uploads run concurrently.
* Duplicate detection at the file layer: If `{sha256}{ext}` already exists, we don't overwrite. Cheap idempotency at the storage layer; the DB layer's unique index on `sha256` is a separate safety net at a different level. 
* Buffer size is 81920: `Stream.CopyToAsync` uses this internally. Matches the tuned default; no reason to pick another number.
* sealed: As always (free perf and enables trivial devirtualization)
* No interface: Same reasoning as DbContext: one implementation, no mocking. 
* GetPath on the same class — everyone who needs the on-disk path (upload response, image serving, thumbnail generation, deletion) goes through this one method. If we ever change the layout (nested subdirs like data/images/ab/cd/{sha}.jpg to avoid one huge folder), we change it in one place.
* IWebHostEnvironment for the root — consistent with Program.cs's ContentRootPath and doesn't require a bespoke config option. If we later want configurable storage root (env var), we swap   the constructor param.
* Content-type mapping lives here — one small switch, three MIME types, matches what the DTO accepts. Endpoint should validate the incoming content-type against the same list before calling StoreAsync; the throw in ExtensionFor is a defense-in-depth net, not the primary check.

## Design Notes on CartoonArchive.Api Endpoint 

* `MapGroup("/api/images/")`: Every route under this prefix inherits from the group - `GET, GET /{id}, DELETE /{id}`. Nice for keeping related routes together and for applying common middleware/auth to the whole group in one place.
* `.DisableAntiforgery()`: .NET 10 enforces antiforgery on form-content endpoints by default. That's the right default for Razor pages posinng cookies-authenticated forms, but this is a raw API endpoint. Once SSO is in plave we will rely on the reverse-proxy's auth headers, not cookies. To keep anti-forgery, we can wire an `X-XSRF-TOKEN` in the frontend later.
* Order of operations: Store first, then check DB. We have to read the request stream to compute the hash anyways, so we might as well let `ImageStorage` do its full temp-write-and-rename. If the DB check reveals a duplicate, the storage call was a no-op (the file was already on the disk from a previous upload). If it is new, we already have the bytes in place before the row is inserted. There is no window where the row points to a non-existent file.
* `Results.Conflict(...)` on duplicate: 409 is the HTTP-correct status for 'conflicts with current state'. Body is the existing cartoon so the UI can navigate to it.
* No race handling: Two simultaneous uploads of the same file would theoretically both pass the existing is null check, and the second `SaveChangesAsync` would fail on the unique index. For a personal archive this is very unlikely; adding a try/catch `DbupdateException` is overhead that pays no rent. If we ever see it, we will add it then.
* `OpenReadStream()` inside `await using`: `IFromFile.OpenReadStream()` returns a fresh readable stream backed by the multipart body (or temp file if the section is large). We dispose it before touching the db so the stream is not held across the DB call.
* `Results.Created($"/api/images/{cartoon.Id}, ...)`: 201 with a `Location` header - standard REST. 
* Handler as a static method taking DI params: .NET automatically resolves them from DI. Static handlers keep the endpoints class from accidentally accumulating state. 
