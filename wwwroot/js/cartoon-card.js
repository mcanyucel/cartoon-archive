window.CartoonCard = (function () {
  const STATUS_PENDING = 0;
  const STATUS_READY = 1;
  const STATUS_FAILED = 2;

  const cache = new Map();
  let currentCartoon = null;
  // Ids in display order — populated by renderList, consumed by lightbox
  // prev/next navigation. Kept separate from the cache Map because the cache
  // survives across re-renders and stale entries would poison the ordering.
  let currentOrder = [];

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
          ${c.snippet ? `<p class="small mb-1">${c.snippet}</p>` : ""}
          <details class="small">
              <summary class="text-muted">Details</summary>
              <p class="mb-0 mt-1">${escapeHtml(c.description ?? "")}</p>
              <p class="text-muted mb-0 mt-1">Uploaded ${escapeHtml(formatUploadedAt(c.uploadedAt))}</p>
          </details>
      `;
  }

  function renderList(cartoons) {
    cache.clear();
    currentOrder = cartoons.map((c) => c.id);
    return cartoons.map(renderCard).join("");
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
                        ${
                          c.status === STATUS_READY
                            ? `<button type="button" class="btn btn-link btn-sm p-0" data-action="share">Share</button>`
                            : ""
                        }
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
      const response = await fetch(`/api/images/${id}/share`, {
        method: "POST",
      });
      if (!response.ok) {
        const err = await response.json().catch(() => ({}));
        if (onMessage)
          onMessage(
            `Share failed: ${err.error ?? response.statusText}`,
            "text-danger",
          );
        return;
      }
      const { url, expiresAt } = await response.json();
      const mins = Math.max(
        1,
        Math.round((new Date(expiresAt) - new Date()) / 60000),
      );

      // Prefer the native OS share sheet when the browser supports it.
      // On installed PWAs (iOS/Android) this opens Messages/WhatsApp/etc.
      if (navigator.share) {
        try {
          await navigator.share({
            title: "Karikatür Arşivi",
            text: `Expires in ${mins} min`,
            url,
          });
          if (onMessage)
            onMessage(`Shared — link expires in ${mins} min`, "text-success");
          return;
        } catch (e) {
          // User closed the share sheet — treat as a no-op, don't fall through.
          if (e.name === "AbortError") {
            if (onMessage) onMessage("", "");
            return;
          }
          // Any other error → fall through to the clipboard fallback below.
        }
      }

      try {
        await navigator.clipboard.writeText(url);
        if (onMessage)
          onMessage(`Link copied — expires in ${mins} min`, "text-success");
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
        const response = await fetch(`/api/images/${id}/retry`, {
          method: "POST",
        });
        if (response.ok) {
          if (onSuccess) await onSuccess();
        } else {
          button.disabled = false;
          const err = await response.json().catch(() => ({}));
          if (onError)
            onError(`Retry failed: ${err.error ?? response.statusText}`);
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

  function setEditMode(modal, editing) {
    const controls = modal.querySelector("#cartoon-lightbox-desc-controls");
    const form = modal.querySelector("#cartoon-lightbox-edit-form");
    if (!controls || !form) return;
    form.classList.toggle("d-none", !editing);
    if (editing) controls.classList.add("d-none");
  }

  function setRegenMode(modal, regenerating) {
    const controls = modal.querySelector("#cartoon-lightbox-desc-controls");
    const form = modal.querySelector("#cartoon-lightbox-regen-form");
    if (!controls || !form) return;
    form.classList.toggle("d-none", !regenerating);
    if (regenerating) controls.classList.add("d-none");
  }

  // Warm the browser cache for the immediate neighbors so arrow-key
  // navigation feels instant instead of blank-frame-then-image.
  function preloadNeighbors(index) {
    for (const delta of [-1, 1]) {
      const id = currentOrder[index + delta];
      if (!id) continue;
      const neighbor = cache.get(id);
      if (neighbor?.imageUrl) {
        const img = new Image();
        img.src = neighbor.imageUrl;
      }
    }
  }

  function navigate(delta) {
    if (!currentCartoon) return;
    const idx = currentOrder.indexOf(currentCartoon.id);
    if (idx === -1) return;
    const nextIdx = idx + delta;
    if (nextIdx < 0 || nextIdx >= currentOrder.length) return;
    const next = cache.get(currentOrder[nextIdx]);
    if (next) openLightbox(next);
  }

  function updateNavButtons(modal) {
    const prevBtn = modal.querySelector("#cartoon-lightbox-prev");
    const nextBtn = modal.querySelector("#cartoon-lightbox-next");
    const idx = currentCartoon ? currentOrder.indexOf(currentCartoon.id) : -1;
    if (prevBtn) prevBtn.disabled = idx <= 0;
    if (nextBtn) nextBtn.disabled = idx === -1 || idx >= currentOrder.length - 1;
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
    setEditMode(modal, false);
    setRegenMode(modal, false);
    updateNavButtons(modal);
    const idx = currentOrder.indexOf(c.id);
    if (idx !== -1) preloadNeighbors(idx);

    // Edit + Regenerate are only meaningful for Ready cartoons.
    const descControls = modal.querySelector("#cartoon-lightbox-desc-controls");
    if (descControls)
      descControls.classList.toggle("d-none", c.status !== STATUS_READY);

    modal.querySelector("#cartoon-lightbox-image").src = c.imageUrl;
    modal.querySelector("#cartoon-lightbox-image").alt = c.description ?? "";
    modal.querySelector("#cartoon-lightbox-title").textContent =
      c.originalFileName ?? "";
    modal.querySelector("#cartoon-lightbox-language").textContent =
      c.language ?? "";

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
    const prevBtn = modal.querySelector("#cartoon-lightbox-prev");
    const nextBtn = modal.querySelector("#cartoon-lightbox-next");
    const editBtn = modal.querySelector("#cartoon-lightbox-edit");
    const editText = modal.querySelector("#cartoon-lightbox-edit-text");
    const editSave = modal.querySelector("#cartoon-lightbox-edit-save");
    const editCancel = modal.querySelector("#cartoon-lightbox-edit-cancel");
    const regenBtn = modal.querySelector("#cartoon-lightbox-regen");
    const regenLang = modal.querySelector("#cartoon-lightbox-regen-lang");
    const regenConfirm = modal.querySelector("#cartoon-lightbox-regen-confirm");
    const regenCancel = modal.querySelector("#cartoon-lightbox-regen-cancel");
    const descEl = modal.querySelector("#cartoon-lightbox-description");
    const descControls = modal.querySelector("#cartoon-lightbox-desc-controls");
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

    prevBtn?.addEventListener("click", () => navigate(-1));
    nextBtn?.addEventListener("click", () => navigate(1));

    // Left/right arrows navigate; ignore when the user is typing in the
    // edit textarea or regen select so keystrokes don't get hijacked.
    function onKeydown(event) {
      const t = event.target;
      if (t && (t.tagName === "TEXTAREA" || t.tagName === "INPUT" || t.tagName === "SELECT"))
        return;
      if (event.key === "ArrowLeft") {
        event.preventDefault();
        navigate(-1);
      } else if (event.key === "ArrowRight") {
        event.preventDefault();
        navigate(1);
      }
    }

    modal.addEventListener("shown.bs.modal", () =>
      document.addEventListener("keydown", onKeydown),
    );
    modal.addEventListener("hidden.bs.modal", () => {
      document.removeEventListener("keydown", onKeydown);
      setConfirmMode(modal, false);
      setEditMode(modal, false);
      setRegenMode(modal, false);
    });

    editBtn?.addEventListener("click", () => {
      if (!currentCartoon || !editText) return;
      setMessage("", "");
      editText.value = currentCartoon.description ?? "";
      setEditMode(modal, true);
    });

    editCancel?.addEventListener("click", () => {
      setEditMode(modal, false);
      if (descControls && currentCartoon?.status === STATUS_READY)
        descControls.classList.remove("d-none");
    });

    editSave?.addEventListener("click", async () => {
      if (!currentCartoon || !editText) return;
      const description = editText.value.trim();
      if (description.length === 0) {
        setMessage("Description cannot be empty.", "text-warning");
        return;
      }
      editSave.disabled = true;
      if (editCancel) editCancel.disabled = true;
      try {
        const response = await fetch(`/api/images/${currentCartoon.id}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ description }),
        });
        if (response.ok) {
          const updated = await response.json();
          currentCartoon = updated;
          cache.set(updated.id, updated);
          if (descEl) descEl.textContent = updated.description ?? "";
          setEditMode(modal, false);
          if (descControls) descControls.classList.remove("d-none");
          setMessage("Description saved.", "text-success");
        } else {
          const err = await response.json().catch(() => ({}));
          setMessage(
            `Save failed: ${err.error ?? response.statusText}`,
            "text-danger",
          );
        }
      } catch (e) {
        setMessage(`Save failed: ${e.message}`, "text-danger");
      } finally {
        editSave.disabled = false;
        if (editCancel) editCancel.disabled = false;
      }
    });

    regenBtn?.addEventListener("click", () => {
      if (!currentCartoon || !regenLang) return;
      setMessage("", "");
      regenLang.value = currentCartoon.language ?? "tr";
      setRegenMode(modal, true);
    });

    regenCancel?.addEventListener("click", () => {
      setRegenMode(modal, false);
      if (descControls && currentCartoon?.status === STATUS_READY)
        descControls.classList.remove("d-none");
    });

    regenConfirm?.addEventListener("click", async () => {
      if (!currentCartoon) return;
      const c = currentCartoon;
      const language = regenLang?.value?.trim() ?? "";
      regenConfirm.disabled = true;
      if (regenCancel) regenCancel.disabled = true;
      try {
        const response = await fetch(`/api/images/${c.id}/regenerate`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(language ? { language } : {}),
        });
        if (response.ok) {
          cache.delete(c.id);
          currentCartoon = null;
          bootstrap.Modal.getOrCreateInstance(modal).hide();
          if (onAfterDelete) await onAfterDelete();
        } else {
          const err = await response.json().catch(() => ({}));
          setMessage(
            `Regenerate failed: ${err.error ?? response.statusText}`,
            "text-danger",
          );
        }
      } catch (e) {
        setMessage(`Regenerate failed: ${e.message}`, "text-danger");
      } finally {
        regenConfirm.disabled = false;
        if (regenCancel) regenCancel.disabled = false;
      }
    });

    confirmBtn?.addEventListener("click", async () => {
      if (!currentCartoon) return;
      const c = currentCartoon;
      setMessage("", "");
      confirmBtn.disabled = true;
      if (cancelBtn) cancelBtn.disabled = true;

      try {
        const response = await fetch(`/api/images/${c.id}`, {
          method: "DELETE",
        });
        if (response.ok) {
          cache.delete(c.id);
          currentCartoon = null;
          bootstrap.Modal.getOrCreateInstance(modal).hide();
          if (onAfterDelete) await onAfterDelete();
        } else {
          const err = await response.json().catch(() => ({}));
          setMessage(
            `Delete failed: ${err.error ?? response.statusText}`,
            "text-danger",
          );
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
    renderList,
    attachRetryHandler,
    attachLightboxHandler,
    attachShareHandler,
  };
})();
