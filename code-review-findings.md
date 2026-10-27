# Code review findings

Review date: 2026-10-27 · against commit `d62dc1e` (v0.5.0) · build clean, 0 warnings.

Scope note: this list deliberately excludes everything already planned in
[next-steps.md](next-steps.md) (rate limiting, search pagination, re-enabling anti-forgery,
metrics endpoint, backup docs, nested storage layout, pre-caption shrink, better
near-duplicate detection, vector search). No overlap with that file.

Ordering is by suggested fix order, not severity alone — items 1, 2 and 4 together give the
most safety per hour.

---

## 1. Bad image bytes → HTTP 500 + permanently orphaned file — FIXED

- [x] Fix
- **Where:** `Api/ImageEndpoint.cs:97-101`, `Storage/ThumbnailGenerator.cs:23`
- **Status:** Confirmed empirically against a running instance, then fixed and re-verified.

### How it was fixed

1. New `Storage/ImageDecodeException.cs`, following the existing `UrlFetchException`
   convention. `ThumbnailGenerator` and `PerceptualHasher` now throw it instead of
   `InvalidOperationException` when `SKBitmap.Decode` returns null.
2. The exact-SHA duplicate check moved **ahead** of thumbnailing in `HandleUpload`. This is
   load-bearing, not just an optimisation: having cleared that check is what proves no
   `Cartoons` row references the stored file, which is what makes deleting it safe.
3. Decode + perceptual hash wrapped in a `try`, catching **only** `ImageDecodeException`.
   An environmental failure (unwritable thumbnail dir, disk full) deliberately still
   propagates, because those bytes may be a perfectly valid image that must not be deleted.
4. `TryDelete` helper added for the rejected-upload path, so a failed cleanup can't mask the
   400 with a 500.

Verified after the fix — all three paths:

| Case | Before | After |
| --- | --- | --- |
| Spoofed content type | 500 + orphaned file on disk | `400 {"error":"File could not be decoded as image/png."}`, no file left behind |
| Valid new PNG | 201 | 201, thumbnail generated |
| Same PNG re-uploaded | 409 | 409, **and the shared file survives** |

That third row is the regression that mattered: cleanup must never fire on the duplicate
path, since there the file legitimately belongs to an existing row.

### Still open from this item

- [ ] **Magic-byte sniffing.** Content type is still taken from the client's claim
      (`ImageEndpoint.cs:68`). Decoding now catches the lie, but only *after* the bytes have
      been written to disk and hashed. Sniffing the header would reject it before any I/O.
- [ ] **`HandleDelete` cleanup isn't actually best-effort.** Its comment says it is, but
      `ImageEndpoint.cs:288-291` calls `File.Delete` unguarded, so an `IOException` there
      throws a 500 *after* the row is already committed. Switching those two calls to the
      new `TryDelete` makes the comment true. Left alone for now to keep this fix focused.

`ImageStorage.StoreAsync` writes the file to disk, then `ThumbnailGenerator.GenerateAsync`
throws `InvalidOperationException` when SkiaSharp's `Decode` returns null. Nothing between
there and the pipeline catches it.

Repro:

```bash
printf 'this is definitely not a png' > fake.png
curl -X POST localhost:5177/api/images -F 'Language=en' -F 'File=@fake.png;type=image/png'
```

Observed:

```
HTTP 500
System.InvalidOperationException: Could not decode image at
  /home/styx/comic-search/data/images/5ec1d30a....png
   at CartoonArchive.Storage.ThumbnailGenerator...GenerateAsync b__0()
   in Storage/ThumbnailGenerator.cs:line 23
```

Two distinct problems from one cause:

1. Client gets a 500 (with full stack trace in Development) where a 400 belongs.
2. `data/images/{sha}.png` is left on disk with **no `Cartoons` row**. It is invisible to
   `HandleDelete`, nothing ever reclaims it, and each retry of a broken file adds another.

Content type is trusted entirely from the client (`f.ContentType`, `ImageEndpoint.cs:68`).
Magic-byte sniffing would reject this before anything touches the disk.

Minimum fix — ordering plus cleanup:

```csharp
var sourcePath = storage.GetPath(sha256, contentType);
try
{
    await thumbnailGenerator.GenerateAsync(sha256, sourcePath, ct);
}
catch (InvalidOperationException)
{
    // Not a decodable image despite the declared content type.
    // Nothing references the file yet, so reclaim it before bailing.
    TryDelete(sourcePath);
    return Results.BadRequest(new { error = "File is not a valid image." });
}
```

**Subtlety to get right:** on the exact-SHA conflict path (`ImageEndpoint.cs:104-108`) the
file is legitimately shared with an existing row. Cleanup must fire **only** for the
pre-insert decode failure, never after a 409.

---

## 2. `RebuildFtsForTags.Down()` references a table called `C` — FIXED

- [x] Fix
- **Where:** `Migrations/20260821094012_RebuildFtsForTags.cs:164` and `:174`
- **Status:** Fixed (`ON C` → `ON Cartoons` in both places) and verified end-to-end.

```sql
CREATE TRIGGER Cartoons_au_del AFTER UPDATE OF Description ON C
```

Truncated copy-paste of `ON Cartoons`. A rollback drops `CartoonsFts` and all triggers
first, then dies on `no such table: C` — leaving the database with no FTS table and no
triggers at all. Only reachable on a rollback, which is precisely when you can't afford it.

One-word fix in two places.

### Verification

A build can't catch this — the SQL lives in a string literal — so it was exercised for real
against a throwaway SQLite file:

```bash
export ConnectionStrings__Default="Data Source=/tmp/scratch/m.db"
dotnet ef database update                  # up to head
dotnet ef database update AddTags          # down through the fixed Down()
```

Both complete. Post-rollback schema is correct:

- `Cartoons_au_del` / `Cartoons_au_ins` exist and target `ON Cartoons`.
- `CartoonsFts` is back to its single-column form (`Description` only, no `TagsText`).

And the old form genuinely was broken — the same statement run directly:

```
Parse error near line 6: no such table: main.C
```

Worth knowing for next time: `dotnet ef migrations script` reads the **compiled** assembly,
so `--no-build` after editing a migration will happily show you the old SQL. Rebuild first.

---

## 3. Search executes the FTS match twice; the snippet half is unbounded

- [ ] Fix
- **Where:** `Api/ImageEndpoint.cs:200-226`
- **Status:** Confirmed by reading.

The row query is `LIMIT 200`. The snippet query runs the identical `MATCH` with **no
limit**, builds a snippet for every matching row, then `GetValueOrDefault` discards
everything outside the 200. A term appearing in most captions costs a full-table snippet
build.

Fold `snippet(CartoonsFts, 0, '%%OPEN%%', '%%CLOSE%%', '…', 15)` into the select list of
the single limited query. Removes the second round trip and makes the search-pagination
item in `next-steps.md` cheaper to implement.

---

## 4. No test project — and the best targets need no infrastructure

- [ ] Add test project
- [ ] Pure-function tests
- [ ] Share-link state machine tests

Biggest structural gap. The security-critical logic is all pure functions, testable with
zero DB or HTTP:

| Target | Why it matters |
| --- | --- |
| `SsrfGuard.IsPrivateOrLocal` (`Storage/UrlImageFetcher.cs:125`) | The CIDR switch, IPv4-mapped-IPv6 normalization, the `fc00::/7` mask, unknown-address-family default-deny. A one-character regression silently reopens SSRF. |
| `BuildFtsMatch` (`Api/ImageEndpoint.cs:258`) | The injection boundary. Needs embedded quotes, `NEAR`/`OR` operator words, `:`, parens, empty-after-strip input. |
| `TagNormalizer.Normalize` (`Tagging/TagNormalizer.cs:19`) | Turkish `ı`→`i` before `FormD`, length caps at both ends, whitespace collapsing. |
| `ReverseProxyAuthMiddleware.StripFingerprint` (`Auth/ReverseProxyAuthMiddleware.cs:87`) | Already caused two bug-fix commits (`7deece3`, `67dce75`). |
| `PerceptualHasher.HammingDistance` (`Storage/PerceptualHasher.cs:61`) | Cheap to pin down. |

Then `WebApplicationFactory` + SQLite for the share-link state machine — worth the second
hour. `HandleView` has three time-dependent branches (active → metadata-only placeholder →
410) and `HandleImage` independently re-enforces `ActiveUntil`. Today, verifying the
redaction actually works means waiting out `TokenTtlHours`. Inject a clock and it becomes
three fast assertions, including the important one: that `/s/{token}/image` stops serving
bytes the moment the window closes.

---

## 5. The `TagsText` invariant is documented but not enforced

- [ ] Fix
- **Where:** `Models/Cartoon.cs:16`, `Data/CartoonArchiveDbContext.cs`

The comment says it "should never be modified from app code; the DB triggers own it." But
it's an ordinary read-write mapped property, so EF will happily write it. Nothing does
today; the first future code path that constructs or updates a `Cartoon` carrying a stale
`TagsText` silently desyncs FTS from the tag tables — search results go quietly wrong with
no error anywhere.

Make the model enforce what the comment promises:

```csharp
cartoon.Property(c => c.TagsText).ValueGeneratedOnAddOrUpdate();
cartoon.Property(c => c.TagsText).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
cartoon.Property(c => c.TagsText).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
```

EF then reads the column and never writes it — exactly the intent.

---

## 6. Upload size cap is asymmetric

- [ ] Fix
- **Where:** `Storage/UrlImageFetcher.cs:19` vs the multipart path in `Api/ImageEndpoint.cs`

URL fetches cap at 20 MB, with a good belt-and-braces streaming check in case
`Content-Length` lied. The multipart path has no cap beyond Kestrel's ~128 MB default. Same
resource, two very different limits.

The check wants to happen **before** `StoreAsync`, since that path streams to disk as it
goes.

---

## 7. Immutable assets served with no cache headers

- [ ] Fix
- **Where:** `Program.cs:108-125`

`/images` and `/thumbnails` are SHA-256-addressed — the bytes behind a URL can never
change. Neither `UseStaticFiles` registration sets `Cache-Control`, so every gallery page
load revalidates all 24 thumbnails.

`OnPrepareResponse` with `public, max-age=31536000, immutable` is a few lines, and is
probably the largest perceived-speed win in the repo — especially on mobile, where the PWA
matters most.

---

## 8. Share URLs trust the Host header

- [ ] Decide + fix
- **Where:** `Program.cs:100-101`, `appsettings.json:29`, `Api/ShareEndpoint.cs:58`

Deliberate and documented: `TrustProxyHeaders=true` clears the forwarded-headers allowlist.
Combined with `AllowedHosts: "*"`, `HandleCreate` falls back to `http.Request.Host` when
`PublicBaseUrl` is unset — so a spoofed `X-Forwarded-Host` mints share links and `og:image`
URLs pointing at someone else's domain.

Two cheap closures, not mutually exclusive:

- Pin `AllowedHosts` to the real hostname.
- Fail startup if `Share:PublicBaseUrl` is unset while `Auth:Mode` is `ReverseProxy`.

The second is nicer: a wrong share URL is otherwise silent until someone clicks it.

---

## 9. Formatting has no enforcement, and has already drifted

- [ ] Add `.editorconfig`
- [ ] Add format check to CI

Everything is consistent CSharpier output **except** `Sharing/ShareCleanupWorker.cs`:
`using  Microsoft.EntityFrameworkCore;` (double space, line 2), trailing whitespace on
several `try` lines, unwrapped 120-col signatures. Compare it to
`Tagging/TagCleanupWorker.cs`, its near-identical sibling, which is wrapped properly.

No `.editorconfig` and no `.csharpierrc` in the repo, so nothing catches it. A committed
config plus a CI format check holds the line for free.

---

## 10. CI only runs on tags

- [ ] Fix
- **Where:** `.github/workflows/release.yml:3-6`

Triggers solely on `v*.*.*`. Nothing validates a push or PR to `main`, so `main` can be
broken and you find out when you cut a release. Add a build job on `push`/`pull_request` —
later running the tests from item 4.

---

## Small things

- [ ] `Captioning/LangaugeNames.cs` — filename typo; the class inside is correctly `LanguageNames`.
- [ ] Trailing spaces inside user-facing error strings: `Api/ImageEndpoint.cs:50`
      (`"...under 16 characters. "`) and `Api/ShareEndpoint.cs:37` (`"...can be shared. "`).
- [ ] `Auth/ReverseProxyAuthMiddleware.cs:71` — "taht" → "that".
- [ ] No `HEALTHCHECK` in the Dockerfile and none in `docker-compose.yml`, despite
      `restart: unless-stopped` — a wedged-but-alive process won't be restarted.
- [ ] `ShareEndpoint.GenerateToken` (`Api/ShareEndpoint.cs:153`) has no collision retry; a
      duplicate is a PK violation surfacing as an unhandled 500. At 56 bits this is
      theoretical — a retry-once is two lines if it ever bothers you.

---

## Scaling note (not a defect)

`Api/ImageEndpoint.cs:115-123` loads **every** `PerceptualHash` row into memory on each
upload and scans them linearly. Fine at a few thousand images; it is the thing that breaks
first as the archive grows. Relevant context for the "Better near-duplicate detection" item
already in `next-steps.md`.

---

## What's already good

Worth recording so it doesn't get refactored away by accident:

- The SSRF guard runs in `SocketsHttpHandler.ConnectCallback`, so it validates the resolved
  IP at connect time and defeats DNS rebinding — not the usual pre-resolve check that
  rebinding walks straight through.
- `BuildFtsMatch` neutralizes FTS5 syntax by phrase-quoting each token rather than
  blocklisting characters.
- Search snippets use `%%OPEN%%`/`%%CLOSE%%` placeholders so the whole string can be
  HTML-escaped before the markers are upgraded to real `<mark>` tags — descriptions can't
  inject HTML this way.
- DataProtection keys are persisted, so antiforgery tokens and auth cookies survive
  container recreation.
- Content-addressed storage with an incremental hash over the upload stream, plus a temp
  file that's moved into place only after the hash is known.
