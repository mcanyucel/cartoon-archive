# Next steps

A loose backlog of things worth doing later. Order is a rough personal priority, not a commitment. Ideas are grouped by theme so it is easier to pick a coherent slice of work when you sit down for an evening.

## User-facing features

### DONE: TAGS

Manual tags on top of LLM captions. Some things captions do not cover well:

- Artist or publication attributes ("Selcuk Erdem", "Penguen")
- Series or collection membership ("xkcd", "huni")
- Curation labels ("favorite", "print-ready", "NSFW")

**Design:**

- **Structured tables, not LLM-generated tags inside the caption.** I am not expecting the LLM to reliably identify anything beyond the most famous artists/sources, and edit/remove semantics on tag text embedded in prose would be painful. `Tags` catalog + `CartoonTags` M2M.
- **Free-typed with autocomplete against the existing catalog.** Existing tags win, a value not in the list creates a new tag on save. Normalization (lowercase + diacritic fold via `TagNormalizer`) canonicalizes `Yiğit Özgür` / `YIGIT OZGUR` / `  yigit ozgur  ` to one row.
- **Unified search: tags flow into FTS5 alongside captions.** A denormalized `TagsText` column on `Cartoons` (kept in sync by triggers on `CartoonTags` INS/DEL and `Tags` UPDATE OF Name) is indexed by FTS5 alongside `Description`. `MATCH 'xkcd table'` naturally returns cartoons tagged `xkcd` that mention `table` — no separate filter UI needed. FTS5's implicit-AND semantics does the "both must be present, anywhere" case for free.
- **PUT full-replace, not delta.** `PUT /api/images/{id}/tags` takes the full desired list; server diffs against current joins. Simpler contract than POST/DELETE add-remove, and matches how the UI already holds state.
- **Chips are just links to `/Search?q=<tag>`.** Cheapest possible tag navigation, reuses the existing search pipeline (which now indexes tags via `TagsText`). Trade-off documented under follow-ups.

**Known follow-ups:**

- Dedicated `/tag/xkcd` page. Chips currently link to `/Search?q=xkcd`, which works but conflates "tagged xkcd" with "the word xkcd appears in the caption." Cheap fix when it matters: a Razor page that queries by tag id directly instead of via FTS.
- Tag rename UI. The `Tags_au` DB trigger handles renames correctly (fans out to every affected cartoon's `TagsText`), but there's no UI to invoke it. Slots into a `/tags` management page next to the orphan-sweep view.
- Theoretical race in `HandleSetTags`: the two `SaveChangesAsync` calls (persist new tags → persist joins) briefly expose new tag rows without joins. `TagCleanupWorker` can't collide with that window at personal scale (24h interval, ms-long endpoint), but wrapping both saves in `BeginTransactionAsync` closes it if bulk-tagging ever becomes contentious.

### DONE: Share links overhaul: OG previews, short URLs, decoupled expiry

Three rough edges around shared links, all rooted in the same problem: `/s/{token}` used to stream raw image bytes, which left no room for HTML `<head>` metadata, drove the 'keywords in URL' workaround for post-expiry readability, and gave chat apps nothing to render as preview cards.

- **No OG previews.** WhatsApp/Discord/Slack link previews come from `og:*` meta tags. A raw image response has no `<head>`, so previews were blank or the app refused to render one at all.
- **Ugly URLs.** Keywords used to live in the path (`/s/…?k=xkcd+programming+humor…`) so that after expiry a human could still tell what the link was about. URLs bloated with URL-encoded unicode and were painful to share in a message.
- **Blank expired page.** After expiry, users got a bare 410 with only a keyword hint parsed from the query string — no context about what the link had been.

The unifying fix: switch `/s/{token}` from "image response" to "HTML response", and everything else follows.

**Design:**

- **HTML page at `/s/{token}`, image bytes at `/s/{token}/image`.** The HTML carries `og:title`, `og:description`, `og:image` (absolute URL), and `twitter:card=summary_large_image`. The sub-route serves the thumbnail (fallback to full-res if absent) — WhatsApp practically wants ≤300 KB and thumbnails cap at 512 px. Both routes stay unauthenticated (already covered by the existing `/s` allowlist in `ReverseProxyAuthMiddleware`); crawlers have no SSO session. The sub-route independently enforces `ActiveUntil` so redaction cannot leak.
- **Short opaque token** (`/s/abc123defG`, 10 chars base64url, ~56 bits from 7 random bytes). No keywords in the path. Title, tags, and description now live in the page `<head>` and body.
- **Two TTLs on the share row.** `ActiveUntil` for the viewable window, `MetadataUntil` (much longer, default `ActiveUntil + 7 days` via `Share:MetadataTtlHours`) for how long the row itself sticks around. Sweep deletes only rows past `MetadataUntil`.
- **`CreatedAt` on ShareToken.** Needed so the placeholder body can honestly show "shared on X". Backfilled on existing rows to `ActiveUntil - 1 hour`, which is the default `TokenTtlHours`.
- **Three render states from one endpoint, each with its own OG treatment:**
  - `now < ActiveUntil` — **200 OK.** Image-only body on a dark background — no caption text, no tag chips, no backlink to the archive. Full OG head so chat previews render with the caption.
  - `ActiveUntil ≤ now < MetadataUntil` — **200 OK.** Placeholder body with the caption, tags, original share date, expiry timestamp, and the metadata-sweep countdown. `og:image` deliberately omitted so the thumbnail is not leaked; `og:title` and `og:description` stay so chat previews still convey what the link used to be.
  - `now ≥ MetadataUntil`, or unknown token — **410 Gone.** Generic body, no OG. Unknown-token and swept-token responses are identical so the endpoint does not disclose which state a token was in.
- **Legacy `?k=` URLs still resolve.** The new handler ignores any query params, so previously-shared links keep working — they just get the new HTML response instead of the old raw-image response, which is a UX upgrade.

**Known follow-ups:**

- Static "expired" placeholder asset for `og:image` in the expired-metadata state. Currently omitted so no thumbnail leaks; a generic branded card would render nicer previews. Ship under `wwwroot/` and reference by absolute URL if you want it.
- `og:image:width/height`. Skipped for now because thumbnails vary in aspect ratio (fit-in 512 px) and lying to crawlers is worse than omitting. Could be added if we start recording thumb dimensions on generation.
- `HintWordCount` option removed; `appsettings.json` no longer includes it. Any user overrides pointing at it are silently ignored.

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
