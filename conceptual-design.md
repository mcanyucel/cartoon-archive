# Karikatür Arşivi — Project Notes

  ## Problem

  Existing self-hosted image search apps (memesearch, Immich, LibrePhotos) don't fit the use case:
  - memesearch: LLM captioning works well but system prompt is hardcoded in English — no Turkish support
  - Immich/LibrePhotos: CLIP-based search misses dialogue/punchlines in speech bubbles
  - Nothing off-the-shelf handles multilingual cartoons/caricatures with text-aware search

  ## Concept

  A minimal self-hosted web app for archiving caricatures and single-scene cartoons in any language,
  with automatic description generation via OpenAI vision API and full-text search over those descriptions.
  The archive is language-agnostic — each image gets described in whatever language the user chooses
  at upload time. Search is equally simple: search in the language you stored the description in.

  ## Architecture

  - **Backend**: ASP.NET Core project scaffolded from `dotnet new webapp` — Razor Pages for the HTML
    shell (gallery, upload page, image detail), Minimal API endpoints for JSON (`POST /api/images`,
    `GET /api/search`, etc.). Mixed in the same project.
  - **Frontend**: Bootstrap 5 (ships with the webapp template in `wwwroot/lib/bootstrap/`), vanilla
    JS as ES modules (`<script type="module">`). No bundler, no `package.json`, no TypeScript on the
    frontend. Delete the templated jQuery bits (`wwwroot/lib/jquery*`) — Bootstrap 5 doesn't need them.
  - **Database**: SQLite + EF Core. FTS5 virtual table + triggers created by hand in a migration;
    queried via `FromSqlRaw`. No repository interface — a concrete `SearchService` is enough for a
    one-backend app.
  - **Search**: SQLite FTS5 with the `unicode61` tokenizer and `remove_diacritics 2` — handles
    Turkish `ı/i/İ`, German umlauts, French accents cleanly across all Latin-script languages with
    a single tokenizer. Trigram considered and rejected (better for substring search, worse for
    phrase queries).
  - **AI**: OpenAI vision API. Model ID configurable via environment variable (full model string,
    not an enum) so it can be swapped without a code change.
  - **Image processing**: SkiaSharp (or Magick.NET) for thumbnails at 256–512px. Originals served
    only on the detail view; gallery uses thumbnails.
  - **Storage**: Filesystem on a Docker volume. Filename = SHA256 of content → free exact-duplicate
    detection and stable URLs. Blobs never go in SQLite.
  - **Auth**: None built in. Sits behind an SSO reverse proxy (Authelia / Authentik / oauth2-proxy)
    that injects `Remote-User` / `Remote-Email` headers. App reads them via a custom
    `AuthenticationHandler`. The reverse proxy must strip these headers from client requests, and
    the app should bind to localhost or a docker-internal network so it's unreachable except via
    the proxy.
  - **Deployment**: Single Docker container, minimal footprint.

  ## Why Not Vector Search

  Vector/semantic search (pgvector, CLIP) finds images *similar* to a query in embedding space.
  For this use case we want to find a *specific* cartoon we remember — FTS on rich LLM descriptions
  is more precise and far simpler. No vector DB needed.

  ## Why Not Local Models

  Florence-2 and similar local models only describe in English. Multilingual local models
  (e.g. qwen2-vl via Ollama) need 6-8GB RAM — too heavy for a VPS alongside other containers.
  A hosted vision API is cheap enough for a personal archive (fractions of a cent per image).

  ## Language Design

  Each image has its own description language, chosen at upload time. No global language lock-in.
  The user is expected to know which language a description was stored in and search accordingly:
  - Search "lonely" → finds images described in English
  - Search "yalnız" → finds images described in Turkish
  - Search "einsam" → finds images described in German

  This is a feature, not a limitation — a multilingual owner gets a multilingual archive.

  Scope note: Latin-script languages only. CJK support is out of scope (would need a different
  FTS5 tokenizer).

  ### System Prompt Template

  Describe this image in {language}. Include any text visible in speech bubbles or captions,
  the characters, their expressions, the setting, and the joke or message if it is a cartoon.

  ### Future Enhancement — Cross-language Search

  Pass the search phrase through a cheap text-model call to get translations in all
  languages present in the archive, then OR the results in FTS5. Deferred to v2.

  ## Upload / Caption Pipeline

  Captioning is async so the UI never blocks on the vision API and images survive container restarts:

  1. Upload writes the image to disk (`{sha256}.{ext}`) and inserts an `Images` row with
     `Status = Pending`.
  2. Thumbnail is generated synchronously (fast, local) and written to disk.
  3. A `BackgroundService` polls for `Pending` rows and calls the vision API with a small
     concurrency limit (1–2, to be nice to the API).
  4. On success: description stored, FTS row inserted via trigger, `Status = Ready`.
  5. On failure: `Status = Failed`, error message stored, manual "retry" button in the UI.

  SQLite is the queue — no Redis or external broker needed. On container restart, the background
  service just picks up any `Pending` / `Failed`-with-retry rows.

  ## Perceptual Deduplication

  SHA256 catches only byte-identical duplicates. Real-world duplicates are usually the same image
  re-encoded, resized, or lightly cropped. On upload, also compute a perceptual hash (pHash via
  `CoenM.ImageHash`, MIT-licensed) — a 64-bit fingerprint from a DCT of a downscaled grayscale
  version. Two visually similar images end up 0–5 bits apart in Hamming distance.

  Store the pHash as a `long` alongside the SHA256. On upload, warn the user if any existing image
  is within a threshold (~5 bits) — don't auto-block, just surface the candidate so the user can
  decide.

  ## Core Features (MVP)

  - [ ] Drag-and-drop image upload (JPG, PNG, WEBP)
  - [ ] Per-image language selector at upload time (default: browser locale)
  - [ ] Async caption generation via vision API on upload; image appears as `Pending` until done
  - [ ] Thumbnail generation on upload (SkiaSharp / Magick.NET)
  - [ ] Store image path + description + language + SHA256 + pHash + status in SQLite
  - [ ] FTS5 full-text search over descriptions (unicode61 + remove_diacritics 2)
  - [ ] Manually edit or regenerate description (with language override)
  - [ ] Retry button for `Failed` captions
  - [ ] Perceptual-duplicate warning at upload time
  - [ ] Simple gallery view (thumbnails only; originals on detail page)
  - [ ] Model ID configurable via environment variable

  ## Nice to Have

  - [ ] Bulk import from directory
  - [ ] Rate limit / upload queue tuning for API tier limits
  - [ ] Cross-language search via translation (v2)
  - [ ] SSO integration verified end-to-end with Authelia (headers auth handler is required, this
        is the integration test)
  - [ ] Backup script (v2 — for now, user's responsibility: `sqlite3 .backup` + rsync the images dir)

  ## Open Questions

  - Which specific vision model to default to — needs a quick benchmark of Turkish caption quality
    across current cheap vision tiers (OpenAI, Anthropic Haiku, others) on ~20 sample images before
    committing.

  ## Cost Estimate

  Cheap vision tier: ~$0.001 per image order of magnitude. A collection of 1000 cartoons ≈ $1 total.
  Re-check pricing at implementation time against whichever model wins the benchmark.

  ## Auth Mode

  The app does not implement its own login, session store, or password hashing. Two supported
  deployment modes selected via `Auth:Mode` (env: `Auth__Mode`):

  - **`ReverseProxy`** (recommended for internet-facing deployments): every gated request must
    carry an authenticated user header (default `X-Remote-User`, configurable via
    `Auth:HeaderName`). A reverse proxy (Authelia, Caddy `basicauth`, nginx `auth_basic`,
    oauth2-proxy) authenticates the user and injects the header. Missing header → 401. The
    proxy is responsible for stripping these headers from incoming client requests; the app
    should bind to localhost or a docker-internal network so it can't be reached except
    through the proxy.
  - **`None`** (default in appsettings — appropriate only for LAN/localhost): no gating, every
    endpoint open. Convenient for development and trusted-network deployments; README calls
    this out as unsafe for internet exposure.

  Wired as a single middleware ahead of endpoints. Path prefixes that must remain reachable
  regardless of mode are allowlisted: `/images/*`, `/thumbnails/*`, `/s/*` (share links), plus
  static assets (`/css`, `/js`, `/lib`). Anyone who does not want SSO but still wants a
  password prompt can put Caddy `basicauth` (or nginx `auth_basic`) in front — same
  `ReverseProxy` mode, header supplied by the proxy.

  ## Time-Limited Share Links

  Every share is temporary. No permanent, un-revocable image URLs on the internet — this is a
  legal/copyright/privacy safety property, not a cryptographic one. A user opens the lightbox,
  clicks Share, gets a URL that expires after ~1 hour.

  ### Endpoints

  - `POST /api/images/{id}/share` (auth-gated when `Auth:Mode = ReverseProxy`). Allowed only
    for `Ready` cartoons — 409 on Pending/Failed since there is no description to build a hint
    from. Creates a token, extracts the hint from the description, returns
    `{ token, expiresAt, url }` where `url` embeds the hint as a query string.
  - `GET /s/{token}` on a valid token — inline-serves the image bytes via `Results.File`.
    Response headers: `Cache-Control: no-store, must-revalidate` (so browsers can't retain
    the image past expiry), `Content-Disposition: inline; filename="cartoon.{ext}"` (generic
    save name). Never references the underlying `/images/{sha256}.ext` URL — DevTools sees
    only `/s/{token}`.
  - `GET /s/{token}` on expired/missing — 410 Gone, renders a minimal HTML page:
    "Link expired. Ask the sender to reshare — hint: 'a man walks into a bar…'". Hint text
    comes from the `k=` query param on the URL, not the database (see below).

  ### Keyword Hint in the URL, Not the DB

  The share URL looks like `https://host/s/{token}?k=a+man+walks+into+a+bar`. The hint is the
  first 5–7 consecutive words from the description, URL-encoded. Consequences:

  - `ShareTokens` stores nothing about the hint — schema is only `Token`, `CartoonId`,
    `ExpiresAt`.
  - Hint survives cartoon deletion, not just token expiry. The URL is self-contained.
  - Nothing extra to garbage-collect for the hint.
  - A recipient who already saw the image once uses the hint to identify which cartoon to
    ask the sender for. Someone who finds the link cold sees a hint but no image — low risk,
    since the intended recipient sees the same content anyway.

  ### Data Model

      ShareTokens {
          Token      TEXT PK    -- 32-char cryptographically random hex
          CartoonId  INT FK -> Cartoons(Id) ON DELETE CASCADE
          ExpiresAt  DATETIMEOFFSET
      }

  ### Cleanup

  A `BackgroundService` sweeps every ~10 minutes deleting rows `WHERE ExpiresAt < now`. No
  opportunistic-on-lookup cleanup: an expired read just returns 410, the sweep catches it
  later. `ON DELETE CASCADE` on the FK keeps the table clean when a cartoon is deleted
  directly.

  ### UI

  A Share button in the lightbox footer. Clicking it POSTs to `/api/images/{id}/share`,
  writes the returned URL to the clipboard via `navigator.clipboard.writeText`, and shows a
  brief inline confirmation ("Link copied — expires in 1 hour"). No new page on the sender
  side; sharing happens entirely from the modal.

  ## Reference

  - Inspired by: https://github.com/neonwatty/meme-search
  - Immich multilingual CLIP (rejected — CLIP misses speech bubble text): https://huggingface.co/collections/immich-app/multilingual-clip
  - SQLite FTS5 docs: https://www.sqlite.org/fts5.html
  - CoenM.ImageHash (pHash for .NET): https://github.com/coenm/ImageHash
