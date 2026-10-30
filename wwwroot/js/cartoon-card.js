window.CartoonCard = (function () {
  const STATUS_PENDING = 0;
  const STATUS_READY = 1;
  const STATUS_FAILED = 2;

  // ---- Navigation sources -------------------------------------------------
  // A source is whatever the lightbox is currently walking through: an ordered
  // list of ids, a way to resolve an id to a full cartoon object, and a hook to
  // fetch more. Two of them exist. The grid source is the page's rendered items,
  // always fully hydrated. The random source (built in random-browse.js) is a
  // seeded permutation of every Ready id with only a sliding window hydrated, so
  // a large archive doesn't mean holding every object in memory.
  //
  // They keep separate Maps deliberately: the gallery's background poll calls
  // renderList, which clears the grid source — that must not pull the floor out
  // from under an open random walk.

  function setOrder(source, ids) {
    source.order = ids;
    source.positions = new Map(ids.map((id, i) => [id, i]));
  }

  function createSource(mode) {
    const source = {
      mode,
      order: [],
      // id -> index. Avoids an O(n) indexOf on every keypress and preload; the
      // random bag can hold the whole archive.
      positions: new Map(),
      items: new Map(),
      indexOf: (id) => source.positions.get(id) ?? -1,
      get: (id) => source.items.get(id),
      put: (c) => source.items.set(c.id, c),
      drop: (id) => source.items.delete(id),
      // Grid items are already on the page. The random source overrides both.
      ensure: () => Promise.resolve(),
      evict: () => {},
    };
    return source;
  }

  const gridSource = createSource("grid");
  let source = gridSource;
  let currentCartoon = null;
  // Set by random browse so it can mirror the cursor into the URL.
  let navigationListener = null;

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

  /// Tag chips as links to the search. `variant` picks the surface they sit on:
  /// "dark" for the viewer's scrim, "light" for panels and page content.
  function renderTagChips(tags, variant = "light") {
    if (!tags || tags.length === 0) return "";
    const cls = variant === "dark" ? "ka-chip" : "ka-tag";
    return tags
      .map(
        (t) =>
          `<a href="/Search?q=${encodeURIComponent(t)}" class="${cls}">${escapeHtml(t)}</a>`,
      )
      .join("");
  }

  /// Caption and tags over the bottom of the thumbnail, revealed on hover. The
  /// grid used to carry a language badge, a Share button, tag chips and a
  /// <details> disclosure on every tile - five controls arguing with the drawing.
  function renderScrim(c) {
    // `snippet` arrives already HTML-escaped by the server with <mark> markers
    // applied afterwards, so it must not be escaped again here.
    const caption = c.snippet ?? escapeHtml(c.description ?? "");
    const chips = renderTagChips(c.tags, "dark");
    if (!caption && !chips) return "";
    return `
        <div class="ka-scrim">
            ${caption ? `<p class="ka-scrim-caption">${caption}</p>` : ""}
            ${chips ? `<div class="ka-chips">${chips}</div>` : ""}
        </div>`;
  }

  /// Pending and failed are real states a browsing session runs into, so they
  /// get a designed overlay rather than a half-empty card.
  function renderTileState(c) {
    if (c.status === STATUS_PENDING) {
      return `<div class="ka-tile-state"><span>captioning…</span></div>`;
    }
    return `
        <div class="ka-tile-state">
            <div>
                <div class="mb-2">caption failed</div>
                <button type="button" class="btn btn-sm btn-outline-primary" data-action="retry">Retry</button>
            </div>
        </div>`;
  }

  function renderList(cartoons) {
    gridSource.items.clear();
    setOrder(
      gridSource,
      cartoons.map((c) => c.id),
    );
    return cartoons.map(renderCard).join("");
  }

  function renderCard(c) {
    gridSource.put(c);
    const ready = c.status === STATUS_READY;
    const cls = `ka-tile${c.status === STATUS_FAILED ? " ka-tile-failed" : ""}`;
    const label = c.description
      ? `Open: ${c.description.slice(0, 80)}`
      : "Open cartoon";

    return `
        <div class="masonry-item" data-id="${c.id}">
            <div class="${cls}" data-action="open" role="button" tabindex="0"
                 aria-label="${escapeHtml(label)}">
                <img src="${c.thumbnailUrl}" alt="${escapeHtml(c.description ?? "")}" loading="lazy">
                ${ready ? renderScrim(c) : renderTileState(c)}
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
    refreshViewerBusy(modal);
  }

  function setEditMode(modal, editing) {
    const controls = modal.querySelector("#cartoon-lightbox-desc-controls");
    const form = modal.querySelector("#cartoon-lightbox-edit-form");
    if (!controls || !form) return;
    form.classList.toggle("d-none", !editing);
    if (editing) controls.classList.add("d-none");
    refreshViewerBusy(modal);
  }

  function setRegenMode(modal, regenerating) {
    const controls = modal.querySelector("#cartoon-lightbox-desc-controls");
    const form = modal.querySelector("#cartoon-lightbox-regen-form");
    if (!controls || !form) return;
    form.classList.toggle("d-none", !regenerating);
    if (regenerating) controls.classList.add("d-none");
    refreshViewerBusy(modal);
  }

  function setTagsMode(modal, editing) {
    const controls = modal.querySelector("#cartoon-lightbox-desc-controls");
    const form = modal.querySelector("#cartoon-lightbox-tags-form");
    if (!controls || !form) return;
    form.classList.toggle("d-none", !editing);
    if (editing) controls.classList.add("d-none");
    refreshViewerBusy(modal);
  }

  // Warm the browser cache for the immediate neighbors so arrow-key
  // navigation feels instant instead of blank-frame-then-image.
  function preloadNeighbors(index) {
    // Deliberately only ±1. This preloads imageUrl, which is the full-res image,
    // not the thumbnail — widening the window means several megabytes fetched
    // speculatively per step, which is exactly wrong on the mobile connections
    // where random browse gets the most use.
    for (const delta of [-1, 1]) {
      const id = source.order[index + delta];
      if (!id) continue;
      const neighbor = source.get(id);
      if (neighbor?.imageUrl) {
        const img = new Image();
        img.src = neighbor.imageUrl;
      }
    }
  }

  // Guards against a held-down arrow key stacking overlapping hydrations.
  let navigating = false;

  async function navigate(delta) {
    if (navigating || !currentCartoon) return;
    const step = delta < 0 ? -1 : 1;
    navigating = true;
    try {
      const idx = source.indexOf(currentCartoon.id);
      if (idx === -1) return;

      let nextIdx = idx + delta;
      // The loop exists only to step over holes: ids the shuffle bag still names
      // but the server no longer has, because they were deleted after the bag
      // was built. A hole is skipped, not a dead end.
      while (nextIdx >= 0 && nextIdx < source.order.length) {
        await source.ensure(nextIdx);
        const next = source.get(source.order[nextIdx]);
        if (next) {
          openLightbox(next);
          return;
        }
        source.order.splice(nextIdx, 1);
        setOrder(source, source.order);
        // Going forward, the next candidate slides into the index we just
        // vacated. Going back, it is one lower.
        if (step < 0) nextIdx -= 1;
      }
    } finally {
      navigating = false;
    }
  }

  // Horizontal swipe on the image area advances the lightbox. Threshold-based
  // so vertical scrolling and pinch-zoom pass through untouched.
  const SWIPE_MIN_DX = 50;
  function attachSwipe(element) {
    let startX = 0;
    let startY = 0;
    let tracking = false;

    element.addEventListener(
      "touchstart",
      (e) => {
        if (e.touches.length !== 1) {
          tracking = false;
          return;
        }
        tracking = true;
        startX = e.touches[0].clientX;
        startY = e.touches[0].clientY;
      },
      { passive: true },
    );

    element.addEventListener(
      "touchend",
      (e) => {
        if (!tracking) return;
        tracking = false;
        const t = e.changedTouches[0];
        const dx = t.clientX - startX;
        const dy = t.clientY - startY;
        if (Math.abs(dx) < SWIPE_MIN_DX) return;
        if (Math.abs(dy) > Math.abs(dx)) return;
        navigate(dx < 0 ? 1 : -1);
      },
      { passive: true },
    );

    element.addEventListener(
      "touchcancel",
      () => {
        tracking = false;
      },
      { passive: true },
    );
  }

  function updateNavButtons(modal) {
    const prevBtn = modal.querySelector("#cartoon-lightbox-prev");
    const nextBtn = modal.querySelector("#cartoon-lightbox-next");
    const idx = currentCartoon ? source.indexOf(currentCartoon.id) : -1;
    const atEnd = idx === -1 || idx >= source.order.length - 1;
    if (prevBtn) prevBtn.disabled = idx <= 0;
    if (nextBtn) nextBtn.disabled = atEnd;

    const random = source.mode === "random";
    const posEl = modal.querySelector("#cartoon-lightbox-position");
    if (posEl) {
      posEl.classList.toggle("d-none", !random || idx === -1);
      if (random && idx !== -1)
        posEl.textContent = `${idx + 1} / ${source.order.length}`;
    }

    // End of the bag is an explicit stop rather than a silent wrap: "nothing
    // repeats" has to stay literally true, so the only way onward is a reshuffle.
    const reshuffleBtn = modal.querySelector("#cartoon-lightbox-reshuffle");
    if (reshuffleBtn)
      reshuffleBtn.classList.toggle("d-none", !(random && atEnd));
    if (random && atEnd) {
      const errEl = modal.querySelector("#cartoon-lightbox-error");
      if (errEl) {
        errEl.textContent = `That's all ${source.order.length} — nothing repeated.`;
        errEl.className = "small me-auto text-muted";
      }
    }
  }

  /// Fades the viewer chrome out once the user stops interacting, so a
  /// full-bleed drawing is not permanently covered top and bottom. Any pointer,
  /// touch or key activity brings it back.
  const IDLE_MS = 2600;

  function attachIdleFade(wrap, modal) {
    let timer = null;

    function wake() {
      wrap.classList.remove("is-idle");
      clearTimeout(timer);
      timer = setTimeout(() => wrap.classList.add("is-idle"), IDLE_MS);
    }

    for (const ev of ["pointermove", "pointerdown", "touchstart", "keydown"]) {
      wrap.addEventListener(ev, wake, { passive: true });
    }
    // Keys are handled at the document level while the modal is open, so the
    // wrap alone would never see arrow-key navigation.
    document.addEventListener("keydown", () => {
      if (modal.classList.contains("show")) wake();
    });

    modal.addEventListener("shown.bs.modal", wake);
    modal.addEventListener("hidden.bs.modal", () => {
      clearTimeout(timer);
      wrap.classList.remove("is-idle", "is-busy");
    });
  }

  /// Pins the chrome while any panel is open. Recomputed from the DOM rather
  /// than tracked as a flag, so closing one panel cannot un-pin the chrome while
  /// another is still showing.
  function refreshViewerBusy(modal) {
    const panelOpen = ["edit", "regen", "tags"].some(
      (k) =>
        modal.querySelector(`#cartoon-lightbox-${k}-form`) &&
        !modal
          .querySelector(`#cartoon-lightbox-${k}-form`)
          .classList.contains("d-none"),
    );
    const confirming = !modal
      .querySelector("#cartoon-lightbox-confirm")
      ?.classList.contains("d-none");
    modal
      .querySelector(".cartoon-lightbox-image-wrap")
      ?.classList.toggle("is-busy", panelOpen || confirming);
  }

  /// Publishes the image's intrinsic width to CSS so the stylesheet can cap how
  /// far a small drawing is allowed to be enlarged. See .ka-viewer-stage img.
  function applyNaturalSize(img) {
    if (!img?.naturalWidth) return;
    img.style.setProperty("--ka-nat-w", String(img.naturalWidth));
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
    // Navigating with the tag editor open used to carry it over to the next
    // cartoon, still holding the previous one's chips.
    setTagsMode(modal, false);
    updateNavButtons(modal);
    const idx = source.indexOf(c.id);
    if (idx !== -1) {
      preloadNeighbors(idx);
      source.evict(idx);
      if (navigationListener) navigationListener(source, idx);
    }

    // Edit + Regenerate are only meaningful for Ready cartoons.
    const descControls = modal.querySelector("#cartoon-lightbox-desc-controls");
    if (descControls)
      descControls.classList.toggle("d-none", c.status !== STATUS_READY);

    const imgEl = modal.querySelector("#cartoon-lightbox-image");
    // Clear before swapping src so the outgoing image's dimensions can't cap
    // the incoming one for the frame before its load event fires.
    imgEl.style.removeProperty("--ka-nat-w");
    imgEl.src = c.imageUrl;
    imgEl.alt = c.description ?? "";
    // A cached image may already be complete, in which case load never fires.
    if (imgEl.complete) applyNaturalSize(imgEl);
    // The original filename is deliberately not shown. For URL uploads it is a
    // hash-like fragment of a remote path and means nothing to a reader.
    modal.querySelector("#cartoon-lightbox-language").textContent =
      c.language ?? "";

    const descEl = modal.querySelector("#cartoon-lightbox-description");
    // Re-clamp on every open: the previous cartoon may have been expanded.
    descEl.className = "ka-viewer-caption is-clamped mb-1";
    if (c.status === STATUS_PENDING) {
      descEl.textContent = "Captioning…";
      descEl.classList.add("fst-italic");
    } else if (c.status === STATUS_FAILED) {
      descEl.textContent = c.errorMessage ?? "Captioning failed.";
      descEl.classList.add("text-danger");
    } else {
      descEl.textContent = c.description ?? "";
    }

    const tagsViewEl = modal.querySelector("#cartoon-lightbox-tags");
    if (tagsViewEl) tagsViewEl.innerHTML = renderTagChips(c.tags, "dark");

    modal.querySelector("#cartoon-lightbox-uploaded").textContent =
      formatUploadedAt(c.uploadedAt);

    bootstrap.Modal.getOrCreateInstance(modal).show();
  }

  function attachLightboxHandler(container, { onAfterDelete } = {}) {
    function openFromEvent(event) {
      // The whole tile opens the viewer now, so the Retry button and the tag
      // links sitting inside it have to be allowed to act on their own.
      if (event.target.closest("button[data-action], a")) return false;
      const tile = event.target.closest('[data-action="open"]');
      if (!tile) return false;
      const item = tile.closest(".masonry-item");
      const id = Number(item?.dataset.id);
      const c = gridSource.get(id);
      if (!c) return false;
      // Clicking a thumbnail always means "walk the grid", even if a random
      // walk was open a moment ago.
      source = gridSource;
      openLightbox(c);
      return true;
    }

    container.addEventListener("click", openFromEvent);

    // The tile is role="button", so it has to answer to Enter and Space too.
    container.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" && event.key !== " ") return;
      if (!event.target.closest('[data-action="open"]')) return;
      if (openFromEvent(event)) event.preventDefault();
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

    // The caption clamps to two lines so it never covers the drawing it
    // describes; clicking it expands, and the next open re-clamps.
    descEl?.addEventListener("click", () => {
      if (descEl.textContent.trim()) descEl.classList.toggle("is-clamped");
    });

    const imgWrap = modal.querySelector(".cartoon-lightbox-image-wrap");
    if (imgWrap) attachSwipe(imgWrap);
    if (imgWrap) attachIdleFade(imgWrap, modal);

    modal
      .querySelector("#cartoon-lightbox-image")
      ?.addEventListener("load", (e) => applyNaturalSize(e.currentTarget));

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
      setTagsMode(modal, false);
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
          source.put(updated);
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
          source.drop(c.id);
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

    // ---- Tag editor ----
    const tagsEditBtn = modal.querySelector("#cartoon-lightbox-tags-edit");
    const tagsChipsEl = modal.querySelector("#cartoon-lightbox-tags-chips");
    const tagsInput = modal.querySelector("#cartoon-lightbox-tags-input");
    const tagsSuggest = modal.querySelector("#cartoon-lightbox-tags-suggest");
    const tagsSave = modal.querySelector("#cartoon-lightbox-tags-save");
    const tagsCancel = modal.querySelector("#cartoon-lightbox-tags-cancel");
    const tagsViewEl = modal.querySelector("#cartoon-lightbox-tags");

    let editingTags = [];
    let acDebounce = null;
    let acAborter = null;
    let acHighlight = -1;

    function paintChips() {
      tagsChipsEl.innerHTML = editingTags
        .map(
          (t, i) =>
            `<span class="ka-tag">${escapeHtml(t)}<button type="button" class="tag-chip-remove" data-remove-index="${i}" aria-label="Remove ${escapeHtml(t)}"><svg aria-hidden="true"><use href="#i-close"></use></svg></button></span>`,
        )
        .join("");
    }

    function closeAc() {
      tagsSuggest.classList.remove("show");
      tagsSuggest.innerHTML = "";
      acHighlight = -1;
    }

    function addTagToEdit(raw) {
      const v = (raw ?? "").trim().toLowerCase();
      if (!v) return;
      if (!editingTags.includes(v)) {
        editingTags.push(v);
        paintChips();
      }
      tagsInput.value = "";
      closeAc();
    }

    async function refreshAc() {
      const prefix = tagsInput.value.trim();
      if (!prefix) return closeAc();
      if (acAborter) acAborter.abort();
      acAborter = new AbortController();
      try {
        const response = await fetch(
          `/api/tags?prefix=${encodeURIComponent(prefix)}&limit=10`,
          { signal: acAborter.signal },
        );
        if (!response.ok) return;
        const data = await response.json();
        const items = (data.items || []).filter(
          (t) => !editingTags.includes(t.name),
        );
        if (!items.length) return closeAc();
        tagsSuggest.innerHTML = items
          .map(
            (t) =>
              `<button type="button" class="dropdown-item" data-tag="${escapeHtml(t.name)}">${escapeHtml(t.name)}</button>`,
          )
          .join("");
        tagsSuggest.classList.add("show");
        acHighlight = -1;
      } catch (e) {
        // AbortError is expected on rapid typing; anything else is non-fatal.
      }
    }

    function updateAcHighlight() {
      const items = tagsSuggest.querySelectorAll(".dropdown-item");
      items.forEach((el, i) => el.classList.toggle("active", i === acHighlight));
    }

    tagsEditBtn?.addEventListener("click", () => {
      if (!currentCartoon) return;
      setMessage("", "");
      editingTags = [...(currentCartoon.tags || [])];
      paintChips();
      tagsInput.value = "";
      closeAc();
      setTagsMode(modal, true);
      tagsInput.focus();
    });

    tagsCancel?.addEventListener("click", () => {
      setTagsMode(modal, false);
      if (descControls && currentCartoon?.status === STATUS_READY)
        descControls.classList.remove("d-none");
    });

    tagsInput?.addEventListener("input", () => {
      clearTimeout(acDebounce);
      acDebounce = setTimeout(refreshAc, 200);
    });

    tagsInput?.addEventListener("keydown", (e) => {
      const items = tagsSuggest.querySelectorAll(".dropdown-item");
      if (e.key === "Enter") {
        e.preventDefault();
        if (acHighlight >= 0 && items[acHighlight]) {
          addTagToEdit(items[acHighlight].dataset.tag);
        } else if (tagsInput.value.trim()) {
          addTagToEdit(tagsInput.value);
        }
      } else if (e.key === "Escape") {
        closeAc();
      } else if (e.key === "ArrowDown" && items.length) {
        e.preventDefault();
        acHighlight = (acHighlight + 1) % items.length;
        updateAcHighlight();
      } else if (e.key === "ArrowUp" && items.length) {
        e.preventDefault();
        acHighlight = acHighlight <= 0 ? items.length - 1 : acHighlight - 1;
        updateAcHighlight();
      } else if (
        e.key === "Backspace" &&
        tagsInput.value === "" &&
        editingTags.length > 0
      ) {
        editingTags.pop();
        paintChips();
      }
    });

    // mousedown (not click) + preventDefault stops the input from blurring
    // before the click lands.
    tagsSuggest?.addEventListener("mousedown", (e) => {
      const btn = e.target.closest(".dropdown-item");
      if (!btn) return;
      e.preventDefault();
      addTagToEdit(btn.dataset.tag);
      tagsInput.focus();
    });

    tagsInput?.addEventListener("blur", () => setTimeout(closeAc, 150));

    tagsChipsEl?.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-remove-index]");
      if (!btn) return;
      const idx = Number(btn.dataset.removeIndex);
      editingTags.splice(idx, 1);
      paintChips();
    });

    tagsSave?.addEventListener("click", async () => {
      if (!currentCartoon) return;
      tagsSave.disabled = true;
      if (tagsCancel) tagsCancel.disabled = true;
      try {
        const response = await fetch(
          `/api/images/${currentCartoon.id}/tags`,
          {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ names: editingTags }),
          },
        );
        if (response.ok) {
          const data = await response.json();
          currentCartoon = { ...currentCartoon, tags: data.tags };
          source.put(currentCartoon);
          if (tagsViewEl)
            tagsViewEl.innerHTML = renderTagChips(currentCartoon.tags, "dark");
          setTagsMode(modal, false);
          if (descControls) descControls.classList.remove("d-none");
          setMessage("Tags saved.", "text-success");
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
        tagsSave.disabled = false;
        if (tagsCancel) tagsCancel.disabled = false;
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
          source.drop(c.id);
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

  /// Hands the lightbox a different source and opens it at `index`. Returns
  /// false when that slot can't be hydrated, so the caller can report it
  /// rather than opening an empty modal.
  async function openSource(newSource, index) {
    source = newSource;
    await source.ensure(index);
    const c = source.get(source.order[index]);
    if (!c) return false;
    openLightbox(c);
    return true;
  }

  function useGridSource() {
    source = gridSource;
  }

  function setNavigationListener(fn) {
    navigationListener = fn;
  }

  return {
    STATUS_PENDING,
    STATUS_READY,
    STATUS_FAILED,
    escapeHtml,
    formatUploadedAt,
    renderCard,
    renderList,
    attachRetryHandler,
    attachLightboxHandler,
    attachShareHandler,
    createSource,
    setOrder,
    openSource,
    useGridSource,
    setNavigationListener,
  };
})();
