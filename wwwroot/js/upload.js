const form = document.getElementById("upload-form");
const status = document.getElementById("upload-status");
const dropZone = document.getElementById("drop-zone");
const fileInput = document.getElementById("file");
const dropHint = document.getElementById("drop-hint");

function setStatus(text, className) {
  status.textContent = text;
  status.className = `mt-2 small ${className}`;
}

if (dropZone && fileInput) {
  const HIGHLIGHT = ["border-primary", "bg-body-tertiary"];

  ["dragenter", "dragover"].forEach((ev) =>
    dropZone.addEventListener(ev, (e) => {
      e.preventDefault();
      dropZone.classList.add(...HIGHLIGHT);
    }),
  );
  ["dragleave", "drop"].forEach((ev) =>
    dropZone.addEventListener(ev, (e) => {
      e.preventDefault();
      dropZone.classList.remove(...HIGHLIGHT);
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
    dropHint.textContent = f ? f.name : "Drop image here, or select a file";
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
  setStatus("Uploading...", "text-muted");

  try {
    const response = await fetch("/api/images", {
      method: "POST",
      body: new FormData(form),
    });

    if (response.status === 201) {
      setStatus("Uploaded. Redirecting…", "text-success");
      form.reset();
      window.location.href = "/";
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
