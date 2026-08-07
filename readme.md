# Cartoon Archive

A self-hosted archive for cartoons and caricatures. Every upload gets an automatic caption from an OpenAI Vision model, and captions become searchable through SQLite FTS5. Written in ASP.NET Core and packaged as a single container.

## Features

* Upload from a local file or a URL, with drag-and-drop support. Accepts JPEG, PNG, and WebP.
* Automatic captions on every upload. Each caption can be edited by hand or regenerated on demand.
* Full-text search over captions with Turkish diacritic folding. Searching for "yalniz" matches "yanlız", and "uber" matches "über".
* Search results highlight the matched terms in context.
* Exact-duplicate rejection at upload time via SHA256. Re-uploading the same bytes returns a reference to the existing item instead of creating a copy.
* Perceptual near-duplicate warning via dHash. In practice this is not very accurate and misses cases you would think are obvious near-matches. It is included as a weak advisory layer that flags what it can, with the intent of replacing it with a stronger algorithm later.
* Temporary sharing via short-lived tokenized URLs. The recipient does not need an account, and each token grants access to a single image for a configurable time-to-live.
* SSRF protection on URL-based uploads, including a specific defense against DNS rebinding. The app resolves the target IP itself and rejects private, loopback, or cloud-metadata addresses at connect time.
* Optional reverse-proxy authentication mode. The app reads the user identity from a header (default `X-Remote-User`) forwarded by whatever SSO gateway is in front of it.
* Dark theme with a toggle. App version shown in the footer.

## Deployment

Two compose files live at the repo root:

* `docker-compose.yml`: bare-metal, app on port 5000, no reverse proxy.
* `docker-compose.caddy.yml`: app behind Caddy with HTTP basic auth.

Both share the `cartoon-data` named volume, so you can switch between them without losing the database. Secrets go in a `.env` file at the repo root, which is git-ignored and docker-ignored.

Images are published to `ghcr.io/mcanyucel/cartoon-archive`. By default the compose files pull the `latest` tag. To pin a specific version, set `APP_VERSION=0.1.0` in `.env`. Both files also keep `build: .` so `docker compose build` on a source checkout produces a locally-tagged image with the same name, useful for iterating without pushing to the registry.

### Bare-metal (`docker-compose.yml`)

```yaml
services:
  app:
    build: .
    image: ghcr.io/mcanyucel/cartoon-archive:${APP_VERSION:-latest}
    container_name: cartoon-archive
    restart: unless-stopped
    ports:
      - "5000:5000"
    volumes:
      - cartoon-data:/app/data
    environment:
      Caption__ApiKey: ${OPENAI_API_KEY:-}

volumes:
  cartoon-data:
```

Run with `docker compose up -d`.

* Named volume, not a bind mount. Bind mounts to `./data` hit WSL2 9P permission quirks between the host and the non-root `app` user (UID 1654) inside the container. A named volume avoids this because Docker inherits ownership from the image's `/app/data` directory, which the Dockerfile pre-chowns.
* No `env_file` directive is needed. Compose auto-loads `.env` from the same directory.
* `Caption__ApiKey` uses the ASP.NET-style `__` section separator, which maps to `Caption:ApiKey` in configuration. Same pattern for any other setting in `appsettings.json` you want to override per environment.

### Behind Caddy with basic auth (`docker-compose.caddy.yml`)

The app is no longer exposed to the host. Only Caddy is. Caddy handles TLS and protects everything with HTTP basic auth.

```yaml
services:
  app:
    build: .
    image: ghcr.io/mcanyucel/cartoon-archive:${APP_VERSION:-latest}
    restart: unless-stopped
    expose:
      - "5000"
    volumes:
      - cartoon-data:/app/data
    environment:
      Caption__ApiKey: ${OPENAI_API_KEY:-}
      Share__PublicBaseUrl: https://${SITE_HOST}
    networks: [web]

  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile.basicauth:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    environment:
      SITE_HOST: ${SITE_HOST}
      BASIC_AUTH_USER: ${BASIC_AUTH_USER}
      BASIC_AUTH_HASH: ${BASIC_AUTH_HASH}
    networks: [web]

volumes:
  cartoon-data:
  caddy-data:
  caddy-config:

networks:
  web:
```

Companion `Caddyfile.basicauth`:

```
{$SITE_HOST} {
    basic_auth {
        {$BASIC_AUTH_USER} {$BASIC_AUTH_HASH}
    }
    reverse_proxy app:5000
}
```

Required `.env`:

```
SITE_HOST=cartoons.example.com
BASIC_AUTH_USER=styx
BASIC_AUTH_HASH=<bcrypt hash>
OPENAI_API_KEY=sk-...
```

Generate the bcrypt hash with:

```
docker run --rm caddy:2-alpine caddy hash-password --plaintext 'yourpassword'
```

Run with `docker compose -f docker-compose.caddy.yml up -d`.

* `expose: "5000"` (not `ports:`) publishes the port only to the Docker network, not the host. Only Caddy's 80 and 443 reach the outside.
* Both services live on a named `web` network, which makes `app` resolvable as the hostname `app` inside the compose network.
* `caddy-data` and `caddy-config` persist ACME certificates and internal state. Without `caddy-data`, every restart re-issues certificates and will trip Let's Encrypt rate limits.
* For local testing without a real domain, use `SITE_HOST=:8080`. Caddy skips ACME on non-domain addresses and serves plain HTTP on that port.
* `Share__PublicBaseUrl` has to be set so share-link URLs point at the public HTTPS host, not the container's internal address.

### Behind your own SSO reverse proxy

If you already run an SSO reverse proxy (Authelia, Keycloak, oauth2-proxy, tinyauth, and so on), the app-side integration is just three env vars:

```yaml
environment:
  Auth__Mode: ReverseProxy
  Auth__HeaderName: Remote-User   # or whatever your SSO forwards
  Share__PublicBaseUrl: https://cartoons.example.com
```

Your proxy config needs to:

* Forward the identity header (`Remote-User` by default) on every request that is not in the public allowlist.
* Bypass auth for these paths, so anonymous share links and static assets still work: `/s/*`, `/css/*`, `/js/*`, `/lib/*`, `/favicon.ico`. The list is kept in sync with `Auth/ReverseProxyAuthMiddleware.cs`.

If the header is missing on a protected path, the app returns 401. It never falls back to a "guest" state.

### Releases

Releases are cut by tagging in git:

```
# bump <Version> in CartoonArchive.csproj
git commit -am "Bump version to 0.1.1"
git tag v0.1.1
git push && git push --tags
```

A GitHub Actions workflow at `.github/workflows/release.yml` picks up the tag, builds a multi-arch (`linux/amd64` and `linux/arm64`) image, and pushes to GHCR with both the version tag and `latest`.


## Design notes

Everything below documents the reasoning behind the implementation. Read it if you plan to extend or modify the code.

### Storage (`ImageStorage`)

It writes bytes to a randomly-named temp file inside `data/images/` while hashing them in the same pass with `IncrementalHash`. When the write is done, we know the SHA256. It computes the final path as `{sha256}{ext}`. If a file with that name already exists (a previous upload of the same bytes), it deletes the temp; otherwise `File.Move` promotes the temp to the final name. It returns the hash. On any exception, it makes a best-effort attempt to delete the temp file.

* Single-pass hash and write via `IncrementalHash`: the alternative is "hash the stream, then re-read to write", which requires a seekable stream (some request bodies aren't) or buffering the whole image in memory (bad for large files). One pass is both simpler and more scalable.
* Temp file plus rename for atomicity: `File.Move` on the same volume is atomic on both Windows and Linux. Readers will never see a partially-written file. Random temp names avoid collisions if two uploads run concurrently.
* Duplicate detection at the file layer: if `{sha256}{ext}` already exists, we don't overwrite. Cheap idempotency at the storage layer. The DB layer's unique index on `Sha256` is a separate safety net at a different level.
* Buffer size is 81920: `Stream.CopyToAsync` uses this internally. Matches the tuned default; no reason to pick another number.
* `sealed`: as always. Free perf and enables trivial devirtualization.
* No interface: same reasoning as with `DbContext`. One implementation, no mocking.
* `GetPath` on the same class: everyone who needs the on-disk path (upload response, image serving, thumbnail generation, deletion) goes through this one method. If we ever change the layout (nested subdirs like `data/images/ab/cd/{sha}.jpg` to avoid one huge folder), we change it in one place.
* `IWebHostEnvironment` for the root: consistent with `Program.cs`'s `ContentRootPath` and doesn't require a bespoke config option. If we later want a configurable storage root (env var), we swap the constructor param.
* Content-type mapping lives here: one small switch, three MIME types, matches what the DTO accepts. The endpoint should validate the incoming content-type against the same list before calling `StoreAsync`. The throw in `ExtensionFor` is a defense-in-depth net, not the primary check.

### API endpoint design (`CartoonArchive.Api`)

* `MapGroup("/api/images/")`: every route under this prefix inherits from the group (`GET`, `GET /{id}`, `DELETE /{id}`). Nice for keeping related routes together and for applying common middleware or auth to the whole group in one place.
* `.DisableAntiforgery()`: .NET 10 enforces antiforgery on form-content endpoints by default. That's the right default for Razor pages posting cookies-authenticated forms, but this is a raw API endpoint. Once SSO is in place we will rely on the reverse-proxy's auth headers, not cookies. To keep anti-forgery, we can wire an `X-XSRF-TOKEN` in the frontend later.
* Order of operations: store first, then check the DB. We have to read the request stream to compute the hash anyway, so we might as well let `ImageStorage` do its full temp-write-and-rename. If the DB check reveals a duplicate, the storage call was a no-op (the file was already on disk from a previous upload). If it is new, we already have the bytes in place before the row is inserted. There is no window where the row points to a non-existent file.
* `Results.Conflict(...)` on duplicate: 409 is the HTTP-correct status for "conflicts with current state". The body is the existing cartoon so the UI can navigate to it.
* No race handling: two simultaneous uploads of the same file would theoretically both pass the "existing is null" check, and the second `SaveChangesAsync` would fail on the unique index. For a personal archive this is very unlikely, and adding a `try/catch DbUpdateException` is overhead that pays no rent. If we ever see it, we will add it then.
* `OpenReadStream()` inside `await using`: `IFormFile.OpenReadStream()` returns a fresh readable stream backed by the multipart body (or a temp file if the section is large). We dispose it before touching the DB so the stream is not held across the DB call.
* `Results.Created($"/api/images/{cartoon.Id}", ...)`: 201 with a `Location` header, standard REST.
* Handler as a static method taking DI params: .NET automatically resolves them from DI. Static handlers keep the endpoints class from accidentally accumulating state.

### Full-text search with FTS5

The virtual table:

```sql
CREATE VIRTUAL TABLE CartoonsFts USING fts5(
    Description,
    content='Cartoons',
    content_rowid='Id',
    tokenize= "unicode61 remove_diacritics 2"
);
```

has the following features:

* `content='Cartoons'`, `content_rowid='Id'`: external-content mode. FTS5 stores only the search index (word to rowid postings). The actual `Description` text lives in the `Cartoons` table. Two upsides: no duplicated text, and we can `SELECT Description FROM CartoonsFts WHERE CartoonsFts MATCH 'foo'` and FTS pulls the text back from `Cartoons` automatically.
* `tokenize = "unicode61 remove_diacritics 2"`: Unicode-aware tokenizer, case-folds, and (with `remove_diacritics 2`) strips diacritics including Turkish `ı, i, İ` and German umlauts. So "yalniz" matches "yanlız", "uber" matches "über", and so on.

#### The four triggers

They keep the FTS index in sync with `Cartoons.Description`:

| Trigger | When | Does |
| --      |  --  | --   |
| Cartoons_ai | AFTER INSERT, if new description is non-null | index the new row |
| Cartoons_ad | AFTER DELETE, if old description was non-null | remove from the index |
| Cartoons_au_del | AFTER UPDATE of Description, if old was non-null | remove old tokens |
| Cartoons_au_ins | AFTER UPDATE of Description, if new is non-null | add new tokens |

* Split into four rather than combined: each does one thing, easier to reason about. The `WHEN` guards mean they no-op when `Description` is null (that is, while a row is still `Pending`).
* `AFTER UPDATE of Description`: the trigger only fires when `Description` is in the UPDATE statement's column list. EF Core by default only updates changed columns, so this keeps unrelated updates (for example `Status = Ready` without changing `Description` in the same statement) from touching the FTS index.
* The `delete` command syntax: that odd-looking `INSERT INTO CartoonsFts(CartoonsFts, rowid, Description) VALUES ('delete', ... )` is FTS5's special-command form for external-content tables. It tells FTS "remove postings for this rowid, using this old text to know which tokens to remove".

#### The `Down` migration

* Drops triggers first, then the FTS table.
* Won't ever run in production but keeps EF's rollback story clean.

#### Query handling

* `FromSql($"...")`: the `$` matters. EF's `FromSql(FormattableString)` extracts `{match}` as a bound parameter (like `@p0`), even though it looks like plain string interpolation. If we use `FromSqlRaw` with `$...` we lose that safety and open ourselves to injection. Rule of thumb: `FromSql` for interpolated, `FromSqlRaw` for hand-built strings with explicit params.
* `ORDER BY rank`: FTS5 exposes `rank` as a hidden column on virtual tables. It's the BM25 score, and lower is better (the default weighting is negative). No `LIMIT` isn't strictly needed but is cheap insurance.
* `WHERE CartoonsFts MATCH ...`: the left side has to be the FTS table name (or its alias in the `FROM`). We used the full name here for readability.
* `SELECT c.*`: `FromSql` requires columns to match the entity mapping. `c.*` on the `Cartoons` table gives us exactly what the `Cartoon` entity expects.

### Image captioning (OpenAI Vision service)

* Reads the file into memory as base64. Vision endpoints accept either a public URL or inline base64 data. For a self-hosted app with no public URL, inline base64 is the practical choice. Cost: doubles the request size in transit (base64 is roughly 1.33x the raw bytes). Fine for images that are already under a few MB after the source resize, a real issue if the originals are 20+ MB. If this bites us, we can always send a shrunk version.
* Anonymous types plus `JsonContent.Create` for the request body: no DTOs needed for the outgoing side. The JSON structure is not reused, so a dedicated class would be dead ceremony.
* `JsonElement` for the response: the reverse reasoning. OpenAI's response schema is broad and we only care about one field (`choices[0].message.content`). Deserializing into a full typed model means writing (and maintaining) types for fields we don't use.
* Explicit `!IsSuccessStatusCode` handling instead of `EnsureSuccessStatusCode()`: we want the error body in the exception message. A 401 with "Invalid API key" is way more useful than the default "response status does not indicate success: 401".
* Throws on empty caption: better to fail loudly than to store empty descriptions that create silent "captioned but no content" bugs.

### Perceptual hashing

A perceptual hash is a fingerprint that stays similar when an image is resized, recompressed, or lightly cropped. It is the opposite of SHA256, which flips entirely for a single byte change. The DCT variant works as follows:

1. Downscale to a small grid (for example 32x32) and convert to grayscale.
2. Apply a Discrete Cosine Transform (DCT) to decompose the image into frequency components.
3. Take the top-left 8x8 block (the low-frequency, "big picture" info).
4. Compute the mean of these 64 values.
5. Each bit of the 64-bit hash is 1 if the corresponding DCT value is above the mean, else 0.

Because we compare only the shape of the low-frequency content, tiny pixel differences don't move most bits. Two images that "look the same" typically end up 0 to 5 bits apart in Hamming distance. Above about 10 bits, they are usually different images.

How we use it: on upload, compute the hash for the new image, scan the DB for existing cartoons within the threshold, and if any match, warn the user (no hard blocking). Since SHA256 already catches byte-identical duplicates, these are the "same image, different encoding or crop" cases.

Honest note on effectiveness: in practice the current dHash-based check misses cases you would expect it to catch, even for images that look nearly identical to a human. It should be treated as a weak advisory layer, not a reliable near-duplicate detector. The interface for the check is in place so we can swap in a stronger algorithm (or a combination of algorithms, or a small learned embedding) without touching the upload flow.

#### Computing the hash

The NuGet library `CoenM.ImageSharp.ImageHash` requires `SixLabors.ImageSharp`. ImageSharp 2.x has open CVEs and 3.x moved to Six Labors' commercial-ish split license. So we use a custom class:

* `SKBitmap.Decode + Resize + GetPixel`: no new dependencies, since thumbnail generation already includes SkiaSharp.
* `GetPixel` is notoriously slow in the general case, but for 72 pixels total it's just noise, and much simpler than fetching the raw pixel span.
* BT.601 luma coefficients (0.299/0.587/0.114) are the standard perceptual weights for R/G/B when computing luminance. They match how humans perceive brightness.
* `Task.Run`: same reason as before, keeps decode and resize off the request thread.
* `HammingDistance` is algorithm-agnostic. Both dHash and pHash produce a 64-bit long, and Hamming distance is meaningful for both.
* dHash is a bit more sensitive than DCT pHash. Instead of the ~5 bit threshold used for pHash, we use ~10 bits as the default for dHash.

### Temporary sharing

* `GenerateToken` uses `RandomNumberGenerator.GetBytes(16)`, that is 128 bits of entropy, which results in 32 hex chars. Pretty unguessable.
* `HtmlEncoder.Default.Encode(hint)`: the hint comes from a URL query param, so an attacker could put `<script>` in it. Encoding neutralizes those attacks. Razor does this automatically for `@Model.X` in Razor pages, but we are building the HTML by hand, so we call it explicitly.
* `Results.File(path, contentType)`: streams the file bytes with the right content type. Headers we set beforehand are preserved.
* Double-checking the file: if the token exists but the underlying file has been deleted, we treat it as expired. Better than returning 500.
* `Results.Content(..., 410)`: 410 Gone is the semantically correct status for "this used to exist and is intentionally no longer available". Search engines drop the URL from their indexes on 410, unlike 404.

### SSRF prevention on URL fetching

Server-Side Request Forgery (SSRF) is what happens when the server makes HTTP requests to a URL that an attacker controls. From the attacker's browser they cannot reach, say, `http://internal-db:5432` or cloud metadata endpoints like `http://169.254.169.254`, but if they get our server to fetch a URL for them, they suddenly can. Some classic attack targets:

* Cloud metadata: `169.254.169.254` on AWS, GCP, and Azure. Returns credentials and IAM tokens.
* Docker-internal services: `http://redis:6379`, `http://postgres:5432`, other containers on the same network.
* Loopback (`127.0.0.1`): reaches services that only listen locally, and reaches the app itself, potentially bypassing auth because the request is "local".

The naive fix is rejecting URLs whose hostname is localhost or a private IP. But DNS rebinding can overcome this:

1. Attacker registers `evil.example.com` and sets its DNS TTL to 0.
2. First DNS lookup (hostname check) returns `1.2.3.4`, a real public IP. Passes.
3. Second DNS lookup (the actual `Socket.ConnectAsync`) returns `127.0.0.1`. The app connects to itself.

A better defense is checking the IP at the moment of connection, not at URL parse time. .NET gives us a hook: `SocketsHttpHandler.ConnectCallback`. We resolve DNS ourselves, reject any private or loopback address in the response, and only then create the socket.

* `HttpCompletionOption.ResponseHeadersRead` returns as soon as we have headers, before the body is downloaded. This lets us reject on content-type or content-length without pulling over the hard limit.
* Double size check: one on the `Content-Length` header (fast) and one on the actual bytes streamed (in cases where the server lied or omitted the header).
* `IPAddress.IsIPv4MappedToIPv6` unwrap: otherwise a malicious `::ffff:127.0.0.1` sneaks past the IPv4 checks.
* `fc00::/7` handled manually: .NET's `IsIPv6SiteLocal` covers the older, deprecated `fec0::/10`, but Unique Local Addresses use `fc00::/7` and there's no built-in.

### Reverse-proxy authentication and forwarded headers

`TrustProxyHeaders` is `false` by default, the safe bare-metal setting. For a deployment behind a reverse proxy, flip it to `true` via the env variable `App__TrustProxyHeaders=true`. By default, `UseForwardedHeaders` only trusts `X-Forwarded-*` from `127.0.0.1` and `::1`. In Docker, the reverse-proxy container has a private IP like `172.19.0.2`. If we don't clear the allow list via `KnownNetworks.Clear()` and `KnownProxies.Clear()`, the forwarded headers get ignored and the fix does nothing. Clearing means **trust everyone**, which is safe here because the app should not be exposed to anyone but the proxy.

### Persisting DataProtection keys

If the app runs in a container, the default path `/home/app/.aspnet/DataProtection-Keys` may not survive container restarts. It lives inside the container's ephemeral layer, not on a persisted volume. Losing it invalidates antiforgery tokens, auth cookies, and anything else encrypted with the key ring.

* `SetApplicationName` isolates key rings if any other ASP.NET app shares the same volume.
* The path resolves to `/app/data/dp-keys` inside the container and sits on the named volume.
* Locally it becomes `./data/dp-keys`, which is already git-ignored via the `data/` rule.
