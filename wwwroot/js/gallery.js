const gallery = document.getElementById("gallery");
const galleryStatus = document.getElementById("gallery-status");
const pager = document.getElementById("gallery-pager");

const POLL_INTERVAL_MS = 3000;
const PAGE_SIZE = 24;
let pollTimer = null;

function setGalleryStatus(text, className) {
  if (!galleryStatus) return;
  galleryStatus.textContent = text;
  galleryStatus.className = `small mb-2 ${className}`;
}

function renderGallery(cartoons) {
  if (cartoons.length === 0) {
    gallery.innerHTML = '<p class="text-muted">No images yet.</p>';
    return;
  }
  gallery.innerHTML = CartoonCard.renderList(cartoons);
}

function renderPager(page, pageSize, total) {
  if (!pager) return;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  if (totalPages <= 1) {
    pager.innerHTML = "";
    return;
  }

  const item = (p, label, opts = {}) => {
    const { active = false, disabled = false, ariaLabel } = opts;
    const cls = `page-item${active ? " active" : ""}${disabled ? " disabled" : ""}`;
    const aria = ariaLabel ? ` aria-label="${ariaLabel}"` : "";
    const href = disabled ? "" : `?page=${p}`;
    return `<li class="${cls}"><a class="page-link" href="${href}"${aria}>${label}</a></li>`;
  };

  // SVG from the sprite in _Layout, not « ‹ › ». A text glyph is placed by its
  // font baseline and side bearings, so it never centres in a square control.
  const icon = (id) => `<svg aria-hidden="true"><use href="#${id}"></use></svg>`;

  const parts = [];
  parts.push(item(1, icon("i-first"), { disabled: page === 1, ariaLabel: "First" }));
  parts.push(item(page - 1, icon("i-prev"), { disabled: page === 1, ariaLabel: "Previous" }));

  // Numeric window of ±2 around the current page.
  const start = Math.max(1, page - 2);
  const end = Math.min(totalPages, page + 2);
  for (let p = start; p <= end; p++) {
    parts.push(item(p, String(p), { active: p === page }));
  }

  parts.push(item(page + 1, icon("i-next"), { disabled: page === totalPages, ariaLabel: "Next" }));
  parts.push(item(totalPages, icon("i-last"), { disabled: page === totalPages, ariaLabel: "Last" }));

  pager.innerHTML = `<ul class="pagination justify-content-center mb-0">${parts.join("")}</ul>`;
}

// Replaces the old "Gallery" heading: the archive's size is the one fact worth
// putting at the top of a page that is otherwise all pictures.
function renderCount(total, totalPages) {
  const el = document.getElementById("gallery-count");
  if (!el) return;
  if (total === 0) {
    el.textContent = "";
    return;
  }
  const drawings = `${total} drawing${total === 1 ? "" : "s"}`;
  el.textContent =
    totalPages > 1 ? `${drawings} · ${totalPages} pages` : drawings;
}

function currentPage() {
  const params = new URLSearchParams(window.location.search);
  const raw = parseInt(params.get("page") ?? "1", 10);
  return isNaN(raw) || raw < 1 ? 1 : raw;
}

function updatePolling(cartoons) {
  const hasPending = cartoons.some((c) => c.status === CartoonCard.STATUS_PENDING);
  if (hasPending && pollTimer === null) {
    pollTimer = setInterval(loadGallery, POLL_INTERVAL_MS);
  } else if (!hasPending && pollTimer !== null) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

async function loadGallery() {
  const page = currentPage();
  try {
    const response = await fetch(`/api/images?page=${page}&pageSize=${PAGE_SIZE}`);
    if (!response.ok) {
      gallery.innerHTML = '<p class="text-danger">Failed to load gallery.</p>';
      return;
    }
    const data = await response.json();

    // Requested past the last page (e.g. after a delete emptied it).
    // Bounce to the last valid page so the user sees content, not "no images".
    const totalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
    if (data.total > 0 && data.items.length === 0 && data.page > totalPages) {
      window.location.href = `?page=${totalPages}`;
      return;
    }

    renderGallery(data.items);
    renderPager(data.page, data.pageSize, data.total);
    renderCount(data.total, totalPages);
    updatePolling(data.items);
  } catch (e) {
    gallery.innerHTML = `<p class="text-danger">Failed to load gallery: ${CartoonCard.escapeHtml(e.message)}</p>`;
  }
}

CartoonCard.attachRetryHandler(gallery, {
  onSuccess: loadGallery,
  onError: (msg) => setGalleryStatus(msg, "text-danger"),
});
CartoonCard.attachLightboxHandler(gallery, { onAfterDelete: loadGallery });
CartoonCard.attachShareHandler(gallery, {
  onMessage: (text, className) => setGalleryStatus(text, className),
});

loadGallery();
