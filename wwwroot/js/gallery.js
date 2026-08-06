const gallery = document.getElementById("gallery");
const galleryStatus = document.getElementById("gallery-status");

const POLL_INTERVAL_MS = 3000;
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
  gallery.innerHTML = cartoons.map(CartoonCard.renderCard).join("");
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
  try {
    const response = await fetch("/api/images");
    if (!response.ok) {
      gallery.innerHTML = '<p class="text-danger">Failed to load gallery.</p>';
      return;
    }
    const cartoons = await response.json();
    renderGallery(cartoons);
    updatePolling(cartoons);
  } catch (e) {
    gallery.innerHTML = `<p class="text-danger">Failed to load gallery: ${CartoonCard.escapeHtml(e.message)}</p>`;
  }
}

CartoonCard.attachRetryHandler(gallery, {
  onSuccess: loadGallery,
  onError: (msg) => setGalleryStatus(msg, "text-danger"),
});
CartoonCard.attachLightboxHandler(gallery);

loadGallery();
