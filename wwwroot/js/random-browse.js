// Random browse: walk the whole archive in a shuffled order, never repeating,
// without the grid's page boundaries.
//
// The permutation lives entirely on the client. /api/images/ids returns every
// Ready id in ascending order; a seeded PRNG shuffles it. Only the shuffled id
// list is held in full — full cartoon objects are hydrated in a sliding window
// through /api/images/batch, so the memory cost stays flat as the archive grows.
//
// The seed is mirrored into the URL (?shuffle=<seed>&at=<n>), which is what makes
// seeding worth doing at all: reload or share the link and the same walk resumes.
// Caveat: the permutation is a function of (seed, id list), so uploading a new
// image changes it. A link shared before an upload resumes somewhere else after.
(function () {
  const button = document.getElementById("random-browse");
  if (!button) return;

  const FETCH_AHEAD = 10; // hydrate this far past the cursor
  const FETCH_BEHIND = 5; // ...and this far behind it, so walking back doesn't
  //                         degenerate into one request per step
  const RUNWAY_MIN = 5; // top up once the hydrated runway ahead drops below this
  const KEEP_BACK = 30; // eviction window behind the cursor
  const KEEP_FWD = 70; // ...and ahead of it — forward is the common direction, so
  //                      a backward scrub keeps what it already passed

  // mulberry32. Small, fast, and statistically fine for shuffling a photo
  // archive; the point is reproducibility from a seed, not cryptographic quality.
  function mulberry32(seed) {
    let a = seed >>> 0;
    return function () {
      a = (a + 0x6d2b79f5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function shuffle(ids, seed) {
    const out = ids.slice();
    const rand = mulberry32(seed);
    for (let i = out.length - 1; i > 0; i--) {
      const j = Math.floor(rand() * (i + 1));
      [out[i], out[j]] = [out[j], out[i]];
    }
    return out;
  }

  function randomSeed() {
    return crypto.getRandomValues(new Uint32Array(1))[0];
  }

  async function fetchIds() {
    const response = await fetch("/api/images/ids");
    if (!response.ok) throw new Error(response.statusText);
    const data = await response.json();
    return data.ids ?? [];
  }

  async function fetchBatch(ids) {
    const response = await fetch(`/api/images/batch?ids=${ids.join(",")}`);
    if (!response.ok) throw new Error(response.statusText);
    const data = await response.json();
    return data.items ?? [];
  }

  function buildSource(order) {
    const source = CartoonCard.createSource("random");
    CartoonCard.setOrder(source, order);

    source.ensure = async function (index) {
      // Serialize. A held-down arrow key must not fire overlapping batches for
      // the same window.
      while (source.inflight) await source.inflight;

      let runway = 0;
      while (
        index + runway < source.order.length &&
        source.items.has(source.order[index + runway])
      )
        runway++;
      // Fetch when the cursor itself is cold (runway 0, which is also every
      // backward step out of the window) or when the forward runway is running
      // short. Otherwise there is nothing to do.
      if (runway > RUNWAY_MIN) return;

      // The band is deliberately two-sided. Hydrating only forward would make a
      // backward walk fetch exactly one id per step, since everything ahead of
      // the cursor is already cached and only order[index] is missing.
      const from = Math.max(0, index - FETCH_BEHIND);
      const to = Math.min(source.order.length, index + FETCH_AHEAD + 1);
      const need = source.order
        .slice(from, to)
        .filter((id) => !source.items.has(id));
      if (need.length === 0) return;

      source.inflight = fetchBatch(need)
        .then((items) => {
          for (const item of items) source.put(item);
        })
        .finally(() => {
          source.inflight = null;
        });
      await source.inflight;
    };

    source.evict = function (index) {
      // Iterate the hydrated items, not the order array — the window is ~100
      // entries while the order can be the whole archive.
      const lo = index - KEEP_BACK;
      const hi = index + KEEP_FWD;
      for (const id of [...source.items.keys()]) {
        const i = source.indexOf(id);
        if (i < lo || i > hi) source.drop(id);
      }
    };

    return source;
  }

  // ---- URL sync ------------------------------------------------------------

  function writeUrl(seed, index) {
    const params = new URLSearchParams(window.location.search);
    params.set("shuffle", seed.toString(36));
    params.set("at", String(index));
    history.replaceState(null, "", `?${params}`);
  }

  function clearUrl() {
    const params = new URLSearchParams(window.location.search);
    if (!params.has("shuffle") && !params.has("at")) return;
    params.delete("shuffle");
    params.delete("at");
    const query = params.toString();
    history.replaceState(null, "", query ? `?${query}` : window.location.pathname);
  }

  function readUrl() {
    const params = new URLSearchParams(window.location.search);
    const raw = params.get("shuffle");
    if (!raw) return null;
    const seed = parseInt(raw, 36);
    if (!Number.isFinite(seed)) return null;
    const at = parseInt(params.get("at") ?? "0", 10);
    return { seed, at: Number.isFinite(at) && at >= 0 ? at : 0 };
  }

  // ---- Wiring --------------------------------------------------------------

  let activeSeed = null;

  CartoonCard.setNavigationListener((source, index) => {
    if (source.mode === "random" && activeSeed !== null)
      writeUrl(activeSeed, index);
  });

  async function start(seed, at) {
    button.disabled = true;
    try {
      const ids = await fetchIds();
      if (ids.length === 0) {
        setStatus("Nothing to browse yet — upload something first.");
        return;
      }

      activeSeed = seed;
      const source = buildSource(shuffle(ids, seed));
      const index = Math.min(at, source.order.length - 1);
      const opened = await CartoonCard.openSource(source, index);
      if (!opened) setStatus("Could not load that image.");
    } catch (e) {
      setStatus(`Random browse failed: ${e.message}`);
    } finally {
      button.disabled = false;
    }
  }

  function setStatus(text) {
    const el = document.getElementById("gallery-status");
    if (!el) return;
    el.textContent = text;
    el.className = "small mb-2 text-danger";
  }

  button.addEventListener("click", () => start(randomSeed(), 0));

  const modal = document.getElementById("cartoon-lightbox");

  // Reshuffle is only reachable at the end of a bag (the lightbox unhides it
  // there). A brand-new seed is the point — it is what makes starting over
  // honest rather than a replay of the same order.
  modal
    ?.querySelector("#cartoon-lightbox-reshuffle")
    ?.addEventListener("click", () => start(randomSeed(), 0));

  // Closing the lightbox ends the walk: hand navigation back to the grid so a
  // later thumbnail click doesn't inherit the shuffled order, and drop the URL
  // params so a refresh shows the plain gallery.
  modal?.addEventListener("hidden.bs.modal", () => {
    if (activeSeed === null) return;
    activeSeed = null;
    CartoonCard.useGridSource();
    clearUrl();
  });

  // Deep link: ?shuffle=… on load resumes that walk.
  const resume = readUrl();
  if (resume) start(resume.seed, resume.at);
})();
