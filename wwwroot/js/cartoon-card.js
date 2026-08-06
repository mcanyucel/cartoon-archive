window.CartoonCard = (function () {
  const STATUS_PENDING = 0;
  const STATUS_READY = 1;
  const STATUS_FAILED = 2;

  const cache = new Map();
  let currentCartoon = null;

  function escapeHtml(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function formatUploadedAt(iso) {
    try {
      return new Date(iso).toLocaleString();
    } catch {
      return iso;
    }
  }

  function renderBody(c) {
    if (c.status === STATUS_PENDING) {
      return `
              <div class="d-flex align-items-center text-muted small">
                  <div class="spinner-border spinner-border-sm me-2" role="status" aria-hidden="true"></div>
                  Captioning…
              </div>
          `;
    }
    if (c.status === STATUS_FAILED) {
      return `
              <details class="small">
                  <summary class="text-danger">Failed</summary>
                  <p class="text-danger mb-0 mt-1">${escapeHtml(c.errorMessage ?? "unknown error")}</p>
                  <p class="text-muted mb-0 mt-1">Uploaded ${escapeHtml(formatUploadedAt(c.uploadedAt))}</p>
              </details>
              <button type="button" class="btn btn-sm btn-outline-primary mt-2" data-action="retry">Retry</button>
          `;
    }
    return `
          <details class="small">
              <summary class="text-muted">Details</summary>
              <p class="mb-0 mt-1">${escapeHtml(c.description ?? "")}</p>
              <p class="text-muted mb-0 mt-1">Uploaded ${escapeHtml(formatUploadedAt(c.uploadedAt))}</p>
          </details>
      `;
  }

  function renderCard(c) {
    cache.set(c.id, c);
    const classes = ["card", "h-100"];
    if (c.status === STATUS_PENDING) classes.push("opacity-75");
    if (c.status === STATUS_FAILED) classes.push("border-danger");

    return `
        <div class="masonry-item" data-id="${c.id}">
            <div class="${classes.join(" ")}">
                <img src="${c.thumbnailUrl}" class="card-img-top"
                     data-action="open" style="cursor: pointer"
                     alt="${escapeHtml(c.description ?? "")}">
                <div class="card-body p-2">
                    <div class="d-flex justify-content-between align-items-center mb-1">
                        <span class="badge bg-secondary">${escapeHtml(c.language)}</span>
                        ${c.status === STATUS_READY
                          ? `<button type="button" class="btn btn-link btn-sm p-0" data-action="share">Share</button>`
                          : ""}
                    </div>
                    ${renderBody(c)}
                </div>
            </div>
        </div>
    `;
  }

  async function performShare(id, { button, onMessage }) {
    if (onMessage) onMessage("", "");
    if (button) button.disabled = true;
    try {
      const response = await fetch(`/api/images/${id}/share`, { method: "POST" });
      if (!response.ok) {
        const err = await response.json().catch(() => ({}));
        if (onMessage)
          onMessage(`Share failed: ${err.error ?? response.statusText}`, "text-danger");
        return;
      }
      const { url, expiresAt } = await response.json();
      try {
        await navigator.clipboard.writeText(url);
        const mins = Math.max(
          1,
          Math.round((new Date(expiresAt) - new Date()) / 60000)
        );
        if (onMessage) onMessage(`Link copied — expires in ${mins} min`, "text-success");
      } catch {
        if (onMessage) onMessage(`Copy failed. URL: ${url}`, "text-muted");
      }
    } catch (e) {
      if (onMessage) onMessage(`Share failed: ${e.message}`, "text-danger");
    } finally {
      if (button) button.disabled = false;
    }
  }

  function attachShareHandler(container, { onMessage } = {}) {
    container.addEventListener("click", async (event) => {
      const button = event.target.closest('button[data-action="share"]');
      if (!button) return;
      const item = button.closest(".masonry-item");
      const id = Number(item?.dataset.id);
      if (!id) return;
      await performShare(id, { button, onMessage });
    });
  }

  function attachRetryHandler(container, { onSuccess, onError } = {}) {
    container.addEventListener("click", async (event) => {
      const button = event.target.closest('button[data-action="retry"]');
      if (!button) return;
      const item = button.closest(".masonry-item");
      const id = item?.dataset.id;
      if (!id) return;

      button.disabled = true;
      if (onError) onError("");
      try {
        const response = await fetch(`/api/images/${id}/retry`, { method: "POST" });
        if (response.ok) {
          if (onSuccess) await onSuccess();
        } else {
          button.disabled = false;
          const err = await response.json().catch(() => ({}));
          if (onError) onError(`Retry failed: ${err.error ?? response.statusText}`);
        }
      } catch (e) {
        button.disabled = false;
        if (onError) onError(`Retry failed: ${e.message}`);
      }
    });
  }

  function setConfirmMode(modal, confirming) {
    const actions = modal.querySelector("#cartoon-lightbox-actions");
    const confirm = modal.querySelector("#cartoon-lightbox-confirm");
    if (!actions || !confirm) return;
    actions.classList.toggle("d-none", confirming);
    actions.classList.toggle("d-flex", !confirming);
    confirm.classList.toggle("d-none", !confirming);
    confirm.classList.toggle("d-flex", confirming);
  }

  function openLightbox(c) {
    const modal = document.getElementById("cartoon-lightbox");
    if (!modal || typeof bootstrap === "undefined") return;

    currentCartoon = c;
    const errEl = modal.querySelector("#cartoon-lightbox-error");
    if (errEl) {
      errEl.textContent = "";
      errEl.className = "small me-auto";
    }
    const shareBtn = modal.querySelector("#cartoon-lightbox-share");
    if (shareBtn) shareBtn.disabled = c.status !== STATUS_READY;
    setConfirmMode(modal, false);

    modal.querySelector("#cartoon-lightbox-image").src = c.imageUrl;
    modal.querySelector("#cartoon-lightbox-image").alt = c.description ?? "";
    modal.querySelector("#cartoon-lightbox-title").textContent = c.originalFileName ?? "";
    modal.querySelector("#cartoon-lightbox-language").textContent = c.language ?? "";

    const descEl = modal.querySelector("#cartoon-lightbox-description");
    if (c.status === STATUS_PENDING) {
      descEl.textContent = "Captioning…";
      descEl.className = "small text-muted fst-italic mb-2";
    } else if (c.status === STATUS_FAILED) {
      descEl.textContent = c.errorMessage ?? "Captioning failed.";
      descEl.className = "small text-danger mb-2";
    } else {
      descEl.textContent = c.description ?? "";
      descEl.className = "small mb-2";
    }

    modal.querySelector("#cartoon-lightbox-uploaded").textContent =
      `Uploaded ${formatUploadedAt(c.uploadedAt)}`;

    bootstrap.Modal.getOrCreateInstance(modal).show();
  }

  function attachLightboxHandler(container, { onAfterDelete } = {}) {
    container.addEventListener("click", (event) => {
      const img = event.target.closest('img[data-action="open"]');
      if (!img) return;
      const item = img.closest(".masonry-item");
      const id = Number(item?.dataset.id);
      const c = cache.get(id);
      if (!c) return;
      openLightbox(c);
    });

    const modal = document.getElementById("cartoon-lightbox");
    if (!modal || modal.dataset.wired) return;
    modal.dataset.wired = "true";

    const deleteBtn = modal.querySelector("#cartoon-lightbox-delete");
    const confirmBtn = modal.querySelector("#cartoon-lightbox-delete-confirm");
    const cancelBtn = modal.querySelector("#cartoon-lightbox-delete-cancel");
    const shareBtn = modal.querySelector("#cartoon-lightbox-share");
    const errEl = modal.querySelector("#cartoon-lightbox-error");

    function setMessage(text, className) {
      if (!errEl) return;
      errEl.textContent = text;
      errEl.className = `small me-auto ${className}`;
    }

    shareBtn?.addEventListener("click", async () => {
      if (!currentCartoon) return;
      await performShare(currentCartoon.id, {
        button: shareBtn,
        onMessage: setMessage,
      });
    });

    deleteBtn?.addEventListener("click", () => {
      setMessage("", "");
      setConfirmMode(modal, true);
    });

    cancelBtn?.addEventListener("click", () => setConfirmMode(modal, false));

    modal.addEventListener("hidden.bs.modal", () => setConfirmMode(modal, false));

    confirmBtn?.addEventListener("click", async () => {
      if (!currentCartoon) return;
      const c = currentCartoon;
      setMessage("", "");
      confirmBtn.disabled = true;
      if (cancelBtn) cancelBtn.disabled = true;

      try {
        const response = await fetch(`/api/images/${c.id}`, { method: "DELETE" });
        if (response.ok) {
          cache.delete(c.id);
          currentCartoon = null;
          bootstrap.Modal.getOrCreateInstance(modal).hide();
          if (onAfterDelete) await onAfterDelete();
        } else {
          const err = await response.json().catch(() => ({}));
          setMessage(`Delete failed: ${err.error ?? response.statusText}`, "text-danger");
          setConfirmMode(modal, false);
        }
      } catch (e) {
        setMessage(`Delete failed: ${e.message}`, "text-danger");
        setConfirmMode(modal, false);
      } finally {
        confirmBtn.disabled = false;
        if (cancelBtn) cancelBtn.disabled = false;
      }
    });
  }

  return {
    STATUS_PENDING,
    STATUS_READY,
    STATUS_FAILED,
    escapeHtml,
    formatUploadedAt,
    renderBody,
    renderCard,
    attachRetryHandler,
    attachLightboxHandler,
    attachShareHandler,
  };
})();
