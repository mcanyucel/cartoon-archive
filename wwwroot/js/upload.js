const form = document.getElementById("upload-form");
const status = document.getElementById("upload-status");
const dropZone = document.getElementById("drop-zone");
const fileInput = document.getElementById("file");
const dropHint = document.getElementById("drop-hint");

function setStatus(text, className) {
  status.textContent = text;
  status.className = `mt-3 small ${className}`;
}

if (dropZone && fileInput) {
  ["dragenter", "dragover"].forEach((ev) =>
    dropZone.addEventListener(ev, (e) => {
      e.preventDefault();
      dropZone.classList.add("is-over");
    }),
  );
  ["dragleave", "drop"].forEach((ev) =>
    dropZone.addEventListener(ev, (e) => {
      e.preventDefault();
      dropZone.classList.remove("is-over");
    }),
  );

  dropZone.addEventListener("drop", (e) => {
    const files = e.dataTransfer?.files;
    if (!files || files.length === 0) return;
    const file = files[0];
    if (!/^image\/(jpeg|png|webp)$/.test(file.type)) {
      setStatus(
        `Unsupported file type: ${file.type || "unknown"}`,
        "text-danger",
      );
      return;
    }
    // Assigning a DataTransfer's files list to an <input type=file> is supported
    // in modern browsers and keeps the form submission path unchanged.
    const dt = new DataTransfer();
    dt.items.add(file);
    fileInput.files = dt.files;
    if (dropHint) dropHint.textContent = file.name;
  });

  fileInput.addEventListener("change", () => {
    if (!dropHint) return;
    const f = fileInput.files?.[0];
    dropHint.textContent = f ? f.name : "Drop an image here, or choose a file";
  });
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  const hasFile = fileInput?.files?.length > 0;
  const urlInput = document.getElementById("url");
  const hasUrl = urlInput?.value?.trim().length > 0;
  if (hasFile === hasUrl) {
    setStatus("Provide either a file or a URL", "text-warning");
    return;
  }
  setStatus("Uploading…", "text-body-secondary");

  try {
    const response = await fetch("/api/images", {
      method: "POST",
      body: new FormData(form),
    });

    if (response.status === 201) {
      const body = await response.json().catch(() => ({}));
      const dups = body?.nearDuplicates ?? [];

      if (dups.length > 0) {
        setStatus("Uploaded, but similar images already exist.", "text-warning");
        showDuplicateWarning(body.cartoon.id, dups);
      } else {
        setStatus("Uploaded. Redirecting…", "text-success");
        form.reset();
        window.location.href = "/";
      }
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

function showDuplicateWarning(uploadedId, dups) {
  const warning = document.getElementById("dup-warning");
  const thumbs = document.getElementById("dup-thumbs");
  const deleteBtn = document.getElementById("dup-delete");
  if (!warning || !thumbs || !deleteBtn) return;

  thumbs.innerHTML = dups
    .map(
      (d) => `
        <a href="/" title="${d.distanceBits}-bit difference">
          <img src="${d.thumbnailUrl}" alt="" style="max-height: 96px; border-radius: 4px;">
        </a>`,
    )
    .join("");
  warning.classList.remove("d-none");

  deleteBtn.onclick = async () => {
    deleteBtn.disabled = true;
    try {
      const res = await fetch(`/api/images/${uploadedId}`, { method: "DELETE" });
      if (res.ok) {
        warning.classList.add("d-none");
        setStatus("Upload deleted.", "text-muted");
        form.reset();
      } else {
        setStatus(`Delete failed: ${res.statusText}`, "text-danger");
      }
    } finally {
      deleteBtn.disabled = false;
    }
  };
}
