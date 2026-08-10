const CACHE_NAME = "karikatur-shell-v3";
const APP_SHELL = [
  "/css/site.css",
  "/js/site.js",
  "/js/cartoon-card.js",
  "/js/gallery.js",
  "/js/search.js",
  "/js/upload.js",
  "/lib/bootstrap/dist/css/bootstrap.min.css",
  "/lib/bootstrap/dist/js/bootstrap.bundle.min.js",
  "/favicon.ico",
  "/favicon-96x96.png",
  "/apple-touch-icon.png",
  "/manifest.json",
];

// redirect: "error" — if any shell URL redirects (e.g. an auth wall
// bouncing us to a login page) the fetch throws and cache install fails
// loudly, instead of silently caching login-page HTML as if it were the
// manifest. Caused a real "NetworkError" on the manifest once when the
// upstream proxy was auth-walling public PWA paths.
self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(CACHE_NAME)
      .then((cache) =>
        cache.addAll(
          APP_SHELL.map((url) => new Request(url, { redirect: "error" })),
        ),
      ),
  );
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k)),
        ),
      )
      .then(() => self.clients.claim()),
  );
});

// Cache-first only for the pre-cached app shell. Everything else — HTML pages,
// /api, /images, /thumbnails — falls through to the network so we never serve
// stale pages or dodge SSO redirects. asp-append-version adds ?v=... to CSS/JS
// URLs, so ignoreSearch lets the cache hit across versions.
self.addEventListener("fetch", (event) => {
  const { request } = event;
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  if (!APP_SHELL.includes(url.pathname)) return;

  event.respondWith(
    caches
      .match(request, { ignoreSearch: true })
      .then((cached) => cached || fetch(request)),
  );
});
