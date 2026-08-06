const form = document.getElementById("upload-form");
const status = document.getElementById("upload-status");

function setStatus(text, className) {
  status.textContent = text;
  status.className = `ms-3 ${className}`;
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
