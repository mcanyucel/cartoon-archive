# Next steps

A loose backlog of things worth doing later. Order is a rough personal priority, not a commitment. Ideas are grouped by theme so it is easier to pick a coherent slice of work when you sit down for an evening.

## User-facing features

### TAGS

Manual tags on top of LLM captions. Some things captions do not cover well:

- Artist or publication attributes ("Selcuk Erdem", "Penguen")
- Series or collection membership ("xkcd", "huni")
- Curation labels ("favorite", "print-ready", "NSFW")

**Design:**

- **Structured tables, not LLM-generated tags inside the caption.** I am not expecting the LLM to reliably identify anything beyond the most famous artists/sources, and edit/remove semantics on tag text embedded in prose would be painful. `tags` catalog + `comic_tags` M2M.
- **Free-typed with autocomplete against the existing catalog.** Existing tags win, a value not in the list creates a new tag on save.
- **Unified search: tags flow into FTS5 alongside captions.** A denormalized `tags_text` column on `comics` (kept in sync by triggers on `comic_tags`) is indexed by FTS5. `MATCH 'xkcd table'` naturally returns comics tagged `xkcd` that mention `table` - no separate filter UI needed. FTS5's implicit-AND semantics does the "both must be present, anywhere" case for free.

**Implementation Steps**

1. Migration: `tags` + `comic_tags` tables, `comics.tags_text NOT NULL DEFAULT ''` column.
2. Drop/recreate `comics_fts` with the new column, backfill from `comics`, replace the existing FTS5 sync triggers (currently description only) with versions that carry `tags_text` too.
3. Add triggers on `comic_tags` (INS/DEL) and `tags` (UPDATE OF name) that recompute `comics.tags_text` for affected comic(s) via `GROUP_CONCAT`.
4. Normalize tag names in app code before insert (`.ToLowerInvariant()` plus a diacritic fold). SQLite's `COLLATE NOCASE` is ASCII-only and will not fold Turkish diacritics (verify?).
5. `TagsRepository`: `Search(prefix)` for autocomplete, `AttachToComic(comicId, names[])`, `DetachFromComic(comicId, names[])` with on-the-fly creation of missing tag rows.
6. UI: tag chips on archive cards and in the lightbox, autocomplete text box on the comic detail/edit page.
7. Orphan sweep in the periodic job: `DELETE FROM tags WHERE id NOT IN (SELECT DISTINCT tag_id FROM comic_tags)`.

### Share links overhaul: OG previews, short URLs, decoupled expiry

Three rough edges around shared links, all rooted in the same problem: `/s/{token}` currently streams raw image bytes, which leaves no room for HTML `<head>` metadata, drives the 'keywords in URL' workaround for post-expiry readability, and gives chat apps nothing to render as preview cards.

- **No OG previews.** WhatsApp/Discord/Slack link previews come from `og:*` meta tags. A raw image response has no `<head>`, so previews are blank or the app refuses to render one at all. Looks suspicious, especially on mobile.
- **Ugly URLs.** Keywords currently live in the path (`/s/xkcd-programming-humor-…`) so that after expiry a human can still tell what the link was about. Works, but URLs bloat with URL-encoded unicode and are painful to share in a message.
- **Blank expired page.** After expiry, users get a bare 404 with no context about what the link used to be.

The unifying fix: switch `/s/{token}` from "image response" to "HTML response", and everything else follows from that.

**Design:**

- **HTML page at `/s/{token}`, image bytes at `/s/{token}/image`.** The HTML carries `og:title`, `og:description`, `og:image` (absolute URL), `og:image:width/height`, and `twitter:card=summary_large_image`. The sub-route serves the thumbnail (not full-res) because WhatsApp practically wants <=300 kB. Both routes stay unauthenticated - crawlers have no SSO session. The sub-route must itself refuse to serve bytes after `active_until`; otherwise redaction leaks.
- **Short opaque token** (`/s/abc123`); no keywords in the path. Title, tags, and description now live in the page `<head>` and `<body>`, not in the url string.
- **Two TTLs on the share row.** `active_until` for the "share is viewable" window, `metadata_until` (much longer, e.g. `active_until + 7 days`) for how long the row itself sticks around. Sweep deletes only rows past `metadata_until`.
- **Three render states from one template, each with its own OG treatment:**
  - `now < active_until` — **200 OK.** Body is minimal: just the image, nicely presented (dark background, comfortable padding). No caption text, no tag chips, no backlink to the archive — the app is not being promoted here. `<head>` carries `og:title` (caption), `og:description`, `og:image` (absolute URL to `/s/{token}/image`), `og:image:width`/`height`, `twitter:card=summary_large_image` for chat previews.
  - `active_until <= now < metadata_until` — **200 OK.** Placeholder body with title, tags, original share date, "expired on X", and honest disclosure that the metadata itself disappears on Y. `<head>` intentionally excludes the thumbnail — the image is redacted from the response, and pointing `og:image` at the thumbnail would leak it. Either omit `og:image` entirely, or point it at a static, generic "expired" placeholder asset. `og:title` and `og:description` may stay so chat previews still convey what the link used to be.
  - `now >= metadata_until` or no row — **410 Gone.** Semantically correct for "existed, deliberately swept" — signals to caches and crawlers that the URL is not coming back (404 means "we don't know", 410 means "it's over"). Minimal body, no OG tags.

**Implementation steps**

1. Migration on the shares table: rename existing expiry to `active_until`, add `metadata_until`.
2. Split `/s/` into two routes. `/s/{token}` returns HTML from a Razor page or minimal template; `/s/{token}/image` returns the thumbnail bytes (fallback to full-res if no thumbnail exists). Both stay unauthenticated. Sub-route must return 410 in the expired and gone states — do not serve the image after `active_until`.
3. Build one HTML template with three render branches (active / expired-with-metadata / gone). Absolute URLs for `og:image`; include `og:image:width`/`height`; `twitter:card=summary_large_image`. Active-state body kept deliberately spare (image only, styled). Optional: ship a static "expired" placeholder asset under `wwwroot/` for the expired-state `og:image`.
4. Switch the share URL generator to a short opaque token; drop keyword embedding from the path.
5. Sweep job: delete rows where `now ≥ metadata_until` (currently deletes past `expires_at`).
6. Legacy long URLs: either keep the old resolver alive in parallel until existing rows drop out naturally, or reissue tokens in a one-shot migration and accept that circulated long URLs will 410 after cutover.
7. Manual smoke test in WhatsApp, Discord, Slack, iMessage — each has its own quirks around image size, HTTPS, and absolute-URL requirements. Worth doing once end-to-end before calling this done.

Small bonus once tags land: the "expired with metadata" body renders tag chips, so a chat recipient can see it was about "xkcd, programming" even after the image itself stops displaying.

### DONE: Turkish morphology: prefix matching in search

FTS5 treats inflected forms as separate tokens, so searching `firavun` misses captions containing `firavunun`, `firavuna`, `firavundan`, etc. Turkish is agglutinative and suffix-heavy, so this bites often in practice.

Fix: in the search query builder, append `*` to each user-entered term before handing it to FTS5. `firavun` becomes `firavun*`, which FTS5 already supports as a prefix query. One-line change, no reindex, no extension.

Accepted trade-offs (documented so they do not come back as surprises later):

- Does not help the reverse case: searching `firavunun` when the caption only has `firavun`. Rare in practice.
- Does not handle stem-changing suffixes (vowel harmony edge cases, consonant softening like `kitap` → `kitabı`). Understandable failures; retry with a shorter root.
- Slightly noisier matches when a short query term happens to be a prefix of an unrelated longer word (e.g., `ad` matches `adam`, `adaylar`). Mitigate with a minimum term length before appending `*` if it becomes annoying.

If this ever stops being enough, the next step is a Snowball Turkish stemmer as an FTS5 tokenizer — both index and query stem to a common root, which fixes both directions and stem changes. Requires shipping a compiled SQLite extension with the .NET app plus a full reindex.

### Pagination on search results

The archive index is paginated (numbered pages, 24 per page, `?page=N` in the URL). Search results still return everything in one shot, capped at 200. Worth revisiting once real usage shows how many results a typical query returns — most searches will be short enough to leave as-is, but a broad "cat" query on a big archive would benefit from the same pager. The client-side pager renderer in `wwwroot/js/gallery.js` is a straightforward lift into a shared helper when the time comes.

The other open design call is **interaction with lightbox navigation (below).** "Next" inside the lightbox should walk across page boundaries transparently, which means the frontend needs either the current page's list plus a fetch-neighbors call at the edges, or a slimmer id-only list of the whole result set cached on open.

### Lightbox navigation: cross-page walk and deep links

Lightbox has prev/next chevrons on the image edges, left/right arrow keys, and preloads the immediate neighbors. "Next" is whichever list rendered the grid — the gallery for `/`, the search results for `/Search?q=…` — so context comes for free from what's on screen.

Two open follow-ups:

- **Walk across page boundaries.** Right now the buttons stop at the first/last item on the current page. Making "next" past the last item auto-load the next page (and prepend/append to the in-memory order) would remove the only rough edge in the UX. Needs either eager id-only prefetch of the full result set on lightbox open, or lazy "load next page when navigation crosses the boundary" logic in the client.
- **URL sync for deep links.** Update the address bar as the user navigates so a refresh or a shared link lands on the currently-viewed image. `history.replaceState` on each transition is the easy part; the harder part is teaching the gallery/search pages to open the lightbox on load when the URL points at an item id.

### Installable as an app (PWA manifest + service worker)

The archive is now installable and can share out to the native OS share sheet. The natural next step is to also register as a share _target_ via `share_target` in `manifest.json`, so images shared _to_ the installed PWA from other apps (Photos, Gallery, WhatsApp) land as uploads. Bigger change — needs an endpoint that accepts `POST /share-target` with a form-encoded file — but it turns the archive into a proper "send it here" destination on the phone.

### Bulk operations

Once the archive grows past a few hundred items, per-item actions get tedious. Reasonable candidates:

- Multi-select in the archive view.
- Bulk delete.
- Bulk re-caption. Useful when you upgrade the model or change the prompt.
- Bulk tag apply/remove (only after tags land).
- Export selected items as a zip.

### Second caption backend

`ICaptionService` was designed as an interface from day one because a second implementation was planned. The most obvious candidate is Anthropic Claude's vision API. Wiring it up is mostly translating the request and response shapes; the DI selection is already there. Consider also a "local" backend that talks to Ollama's OpenAI-compat mode so no external API is needed at all.

## Search and deduplication

### Better near-duplicate detection

The current dHash-based check is weak enough that it deserves replacement (see the readme's honest note on effectiveness). Two paths:

- Try a stronger classical algorithm: DCT-based pHash (properly implemented), wavelet hash, or an ensemble of two hashes with a combined threshold. Cheap to implement.
- Compute a small image embedding (CLIP-style, or a smaller model like MobileNet) and use nearest-neighbor lookup. For an archive under a few thousand images, a simple in-memory linear scan is fine. Above that, a lightweight vector index like `sqlite-vss` or `qdrant` running as another container.

The `PerceptualHasher` interface is already there, so whichever route you pick, the change stays localized.

### Vector-based semantic search

Current search is FTS5 keyword search over caption text. In practice this behaves like semantic search because captions are already written in "what the image shows" style. But as the archive grows past several thousand items, or when users search for concepts described in different words by different captions (say "kid on scooter" vs "child on skateboard"), keyword search will start missing things.

Path: compute an embedding for each caption (or each image directly), store in a small vector index, run FTS5 and vector similarity in parallel, and merge the top results with a small ranking model or just a weighted score. Keep FTS5 as the primary because it is fast and precise for exact matches.

## Security and robustness

### Rate limiting on upload and caption

Even with SSO in front, an in-app rate limiter on the upload and caption endpoints protects against:

- A single authenticated user going wild (deliberately or with a runaway script).
- An SSO provider misconfiguration that lets a burst of requests through unauthenticated.
- An accidentally-exposed instance getting hit by a scanner before you notice.

ASP.NET Core's built-in `AddRateLimiter` middleware can enforce per-user or global caps in roughly ten lines of code. Sensible defaults: something like 30 uploads per user per hour, 60 captions per hour globally.

### Re-enable anti-forgery on the API endpoints

`/api/images/*` currently uses `.DisableAntiforgery()` because the original design assumed no cookie-authenticated form flow. Once SSO is in place and session cookies exist, wire an `X-XSRF-TOKEN` header in the frontend fetch calls and drop the `.DisableAntiforgery()` call. Defense-in-depth against a malicious authenticated user tricking another authenticated user's browser into making a cross-site request.

## Ops and deployment

### Backup and restore documentation

The database and images all live under `/app/data`, so a backup is just a snapshot of that directory. Two workflows worth documenting:

- **Cold copy:** `docker compose down`, `tar czf backup.tar.gz -C /var/lib/docker/volumes/cartoon-data _data`, `docker compose up -d`. Simple, safe, but has downtime.
- **Hot copy:** SQLite's `.backup` command produces a consistent snapshot of the DB while the app is running. Combine with `rsync` over the images directory (which is append-only in normal use) for a fully live backup.

Both belong in the readme's Deployment section as a subsection.

### Pre-caption image shrink

The vision service base64-encodes the whole image. For originals above ~5 MB this starts to matter, both the base64 blowup (~1.33x) and the vision API's own per-image token cost. Add a pre-caption step that downsamples to a fixed max dimension (1024 or 1536) before sending, keeping the original on disk untouched. `ThumbnailGenerator` already has the SkiaSharp pipeline, so factor out a common resize helper.

### Nested image storage layout

When `data/images/` grows past a few thousand files, some filesystems get slow at directory listings and Docker volume backups slow down. Move to a two-level layout: `data/images/ab/cd/abcd1234...jpg`. `ImageStorage.GetPath` is the single place that computes paths, so this is a one-method change plus a small migration script to move existing files.

### Metrics endpoint

A `/metrics` endpoint in Prometheus format would make the app friendlier to run alongside other self-hosted services with an existing monitoring stack. .NET has `OpenTelemetry.Exporter.Prometheus` for this. Optional at first, gated behind a config toggle so single-user deploys can ignore it entirely.
