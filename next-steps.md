# Next steps

A loose backlog of things worth doing later. Order is a rough personal priority, not a commitment. Ideas are grouped by theme so it is easier to pick a coherent slice of work when you sit down for an evening.

## User-facing features

### Turkish morphology: prefix matching in search

FTS5 treats inflected forms as separate tokens, so searching `firavun` misses captions containing `firavunun`, `firavuna`, `firavundan`, etc. Turkish is agglutinative and suffix-heavy, so this bites often in practice.

Fix: in the search query builder, append `*` to each user-entered term before handing it to FTS5. `firavun` becomes `firavun*`, which FTS5 already supports as a prefix query. One-line change, no reindex, no extension.

Accepted trade-offs (documented so they do not come back as surprises later):

* Does not help the reverse case: searching `firavunun` when the caption only has `firavun`. Rare in practice.
* Does not handle stem-changing suffixes (vowel harmony edge cases, consonant softening like `kitap` → `kitabı`). Understandable failures; retry with a shorter root.
* Slightly noisier matches when a short query term happens to be a prefix of an unrelated longer word (e.g., `ad` matches `adam`, `adaylar`). Mitigate with a minimum term length before appending `*` if it becomes annoying.

If this ever stops being enough, the next step is a Snowball Turkish stemmer as an FTS5 tokenizer — both index and query stem to a common root, which fixes both directions and stem changes. Requires shipping a compiled SQLite extension with the .NET app plus a full reindex.

### Pagination on the archive index

The index currently loads nearly every item at once, which was tolerable at small sizes but already makes cold loads noticeably heavy and only gets worse as the archive grows. Go with **numbered pages** rather than infinite scroll: content changes rarely, so bookmarkability, shareable links, and "know where you are" matter more than the smooth-feed feel of IS.

Sketch:

* Server-side paging on the index query with `Skip`/`Take`; page size around 24-48 so it fills a couple of rows on desktop without a huge payload.
* Page number in the URL as `?page=N` so refresh, browser back/forward, and shared links all resolve to the same view. Default to page 1 when absent, clamp out-of-range values.
* Pager component at the bottom: first, prev, next, last, plus a small window of numeric links around the current page. Reuse the same component on the search results view.
* Total count query (`COUNT(*)`) alongside the page fetch so the pager can render the last-page link. Cheap on SQLite for anything under six figures.

Design calls to make:

* **Whether search results paginate too, or stay as a single scroll.** Consistency argues for both paginating, but search results are often short enough that a single page is fine. Worth deciding once real usage shows how many results a typical query returns.
* **Interaction with lightbox navigation (below).** "Next" inside the lightbox should walk across page boundaries transparently, which means the frontend needs either the current page's list plus a fetch-neighbors call at the edges, or a slimmer id-only list of the whole result set cached on open.

### Lightbox navigation (prev/next + keyboard arrows)

Right now clicking an image opens the detail view for that one item, and viewing another one means going back to the archive. Turn the lightbox into a browsing surface instead:

* On-screen prev/next affordances on the left and right edges of the lightbox.
* Keyboard: left and right arrow keys to move between items, escape to close (if it is not already wired).
* Preload the immediate neighbors (one or two in each direction) so arrow presses feel instant instead of blank-frame-then-image.

Design calls to make:

* **What defines "next"?** The natural answer is: whichever list brought you here. If you clicked from a search results view, next means the next matching item; if you clicked from the main archive, next means the next by upload order. This implies the lightbox needs to know its parent context, either via a query string on the item URL or a small in-memory list cached in the SPA-ish frontend state.
* **URL sync.** Update the address bar as the user navigates so a refresh or a shared link lands on the currently-viewed image, not the one they originally opened. `history.replaceState` on each transition, no full navigation.

Small feature, big quality-of-life boost once the archive has more than a screenful of items.

### Installable as an app (PWA manifest + service worker)

Make the archive installable on desktop and mobile so it launches like a native app instead of just a browser tab. Two small pieces:

* **Web app manifest** (`wwwroot/manifest.json`): icons, theme colors, display mode (`standalone`), start URL. This is what triggers the browser's "Add to Home Screen" prompt.
* **Service worker**: register a minimal one from `_Layout.cshtml` that caches the app shell (HTML, CSS, JS, favicon). Even without offline write support, this alone is enough to make the install experience work and speeds up cold loads. Real offline write is not worth chasing for a personal archive.

Reference points inside the current project: manifest goes next to `favicon.ico` and needs a `<link rel="manifest">` in the layout. Icons at 192x192 and 512x512 are the two Chrome insists on.

### Tags

Manual tags on top of LLM captions. Some things captions do not cover well:

* Artist or publication attribution ("Selçuk Erdem", "Penguen dergisi")
* Series or collection membership
* Curation labels ("favorite", "print-ready", "NSFW")

Sketch: `Tag` table with a many-to-many join to `Cartoons`. Two design calls to make:

* Whether tag text goes into the FTS index (so `#turkish` shows up when searching "turkish"), or stays as a separate filter that ANDs with the search query. The second is cleaner but less discoverable.
* Whether tags are freeform or come from a controlled list. Freeform is easier but eventually collects `#favorite`, `#favourite`, `#fav` all pointing at the same intent.

### Bulk operations

Once the archive grows past a few hundred items, per-item actions get tedious. Reasonable candidates:

* Multi-select in the archive view.
* Bulk delete.
* Bulk re-caption. Useful when you upgrade the model or change the prompt.
* Bulk tag apply/remove (only after tags land).
* Export selected items as a zip.

### Second caption backend

`ICaptionService` was designed as an interface from day one because a second implementation was planned. The most obvious candidate is Anthropic Claude's vision API. Wiring it up is mostly translating the request and response shapes; the DI selection is already there. Consider also a "local" backend that talks to Ollama's OpenAI-compat mode so no external API is needed at all.

## Search and deduplication

### Better near-duplicate detection

The current dHash-based check is weak enough that it deserves replacement (see the readme's honest note on effectiveness). Two paths:

* Try a stronger classical algorithm: DCT-based pHash (properly implemented), wavelet hash, or an ensemble of two hashes with a combined threshold. Cheap to implement.
* Compute a small image embedding (CLIP-style, or a smaller model like MobileNet) and use nearest-neighbor lookup. For an archive under a few thousand images, a simple in-memory linear scan is fine. Above that, a lightweight vector index like `sqlite-vss` or `qdrant` running as another container.

The `PerceptualHasher` interface is already there, so whichever route you pick, the change stays localized.

### Vector-based semantic search

Current search is FTS5 keyword search over caption text. In practice this behaves like semantic search because captions are already written in "what the image shows" style. But as the archive grows past several thousand items, or when users search for concepts described in different words by different captions (say "kid on scooter" vs "child on skateboard"), keyword search will start missing things.

Path: compute an embedding for each caption (or each image directly), store in a small vector index, run FTS5 and vector similarity in parallel, and merge the top results with a small ranking model or just a weighted score. Keep FTS5 as the primary because it is fast and precise for exact matches.

## Security and robustness

### Rate limiting on upload and caption

Even with SSO in front, an in-app rate limiter on the upload and caption endpoints protects against:

* A single authenticated user going wild (deliberately or with a runaway script).
* An SSO provider misconfiguration that lets a burst of requests through unauthenticated.
* An accidentally-exposed instance getting hit by a scanner before you notice.

ASP.NET Core's built-in `AddRateLimiter` middleware can enforce per-user or global caps in roughly ten lines of code. Sensible defaults: something like 30 uploads per user per hour, 60 captions per hour globally.

### Re-enable anti-forgery on the API endpoints

`/api/images/*` currently uses `.DisableAntiforgery()` because the original design assumed no cookie-authenticated form flow. Once SSO is in place and session cookies exist, wire an `X-XSRF-TOKEN` header in the frontend fetch calls and drop the `.DisableAntiforgery()` call. Defense-in-depth against a malicious authenticated user tricking another authenticated user's browser into making a cross-site request.

## Ops and deployment

### Backup and restore documentation

The database and images all live under `/app/data`, so a backup is just a snapshot of that directory. Two workflows worth documenting:

* **Cold copy:** `docker compose down`, `tar czf backup.tar.gz -C /var/lib/docker/volumes/cartoon-data _data`, `docker compose up -d`. Simple, safe, but has downtime.
* **Hot copy:** SQLite's `.backup` command produces a consistent snapshot of the DB while the app is running. Combine with `rsync` over the images directory (which is append-only in normal use) for a fully live backup.

Both belong in the readme's Deployment section as a subsection.

### Pre-caption image shrink

The vision service base64-encodes the whole image. For originals above ~5 MB this starts to matter, both the base64 blowup (~1.33x) and the vision API's own per-image token cost. Add a pre-caption step that downsamples to a fixed max dimension (1024 or 1536) before sending, keeping the original on disk untouched. `ThumbnailGenerator` already has the SkiaSharp pipeline, so factor out a common resize helper.

### Nested image storage layout

When `data/images/` grows past a few thousand files, some filesystems get slow at directory listings and Docker volume backups slow down. Move to a two-level layout: `data/images/ab/cd/abcd1234...jpg`. `ImageStorage.GetPath` is the single place that computes paths, so this is a one-method change plus a small migration script to move existing files.

### Metrics endpoint

A `/metrics` endpoint in Prometheus format would make the app friendlier to run alongside other self-hosted services with an existing monitoring stack. .NET has `OpenTelemetry.Exporter.Prometheus` for this. Optional at first, gated behind a config toggle so single-user deploys can ignore it entirely.
