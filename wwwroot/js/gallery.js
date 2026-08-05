const gallery = document.getElementById("gallery");
const form = document.getElementById("upload-form");
const status = document.getElementById("upload-status");

const STATUS_LABELS = {
  0: "Pending",
  1: "Ready",
  2: "Failed",
};

function escapeHtml(str) {
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function setStatus(text, className) {
  status.textContent = text;
  status.className = `ms-3 ${className}`;
}

function renderGallery(cartoons) {
  if (cartoons.length === 0) {
    gallery.innerHTML = '<p class="text-muted">No images yet.</p>';
    return;
  }
  gallery.innerHTML = cartoons
    .map(
      (c) => `
        <div class="col">
            <div class="card h-100">
                <img src="${c.imageUrl}" class="card-img-top" alt="${escapeHtml(c.originalFileName)}">
                <div class="card-body p-2">
                    <p class="card-text small mb-0 text-truncate" title="${escapeHtml(c.originalFileName)}">
                        ${escapeHtml(c.originalFileName)}
                    </p>
                    <p class="card-text small text-muted mb-0">
                        ${c.language} · ${STATUS_LABELS[c.status] ?? "Unknown"}
                    </p>
                </div>
            </div>
        </div>
    `,
    )
    .join("");
}

async function loadGallery() {
  try {
    const response = await fetch("/api/images");
    if (!response.ok) {
      gallery.innerHTML = '<p class="text-danger">Failed to load gallery.</p>';
      return;
    }
    renderGallery(await response.json());
  } catch (e) {
    gallery.innerHTML = `<p class="text-danger">Failed to load gallery: ${escapeHtml(e.message)}</p>`;
  }
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  setStatus("Uploading...", "text-muted");

  try {
    const response = await fetch("/api/images", {
      method: "POST",
      body: new FormData(form),
    });

    if (response.status === 201) {
      setStatus("Uploaded.", "text-success");
      form.reset();
      await loadGallery();
    } else if (response.status === 409) {
      setStatus("This image is already in the archive.", "text-warning");
    } else {
      const err = await response.json().catch(() => ({}));
      setStatus(
        `Upload failed: ${err.error ?? response.statusText}`,
        "text-danger",
      );
    }
  } catch (e) {
    setStatus(`Upload failed: ${e.message}`, "text-danger");
  }
});

loadGallery();
