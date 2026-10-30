(function () {
  const toggle = document.getElementById("theme-toggle");
  const icon = document.getElementById("theme-icon");

  // Repoints the <use> at a sprite symbol rather than swapping an emoji: the
  // emoji rendered at whatever size and baseline the platform font chose, which
  // is why it never sat centred in its button.
  function updateIcon(theme) {
    const use = icon?.querySelector("use");
    if (use) use.setAttribute("href", theme === "dark" ? "#i-sun" : "#i-moon");
  }

  updateIcon(document.documentElement.getAttribute("data-bs-theme"));

  if (toggle) {
    toggle.addEventListener("click", () => {
      const current = document.documentElement.getAttribute("data-bs-theme");
      const next = current === "dark" ? "light" : "dark";
      document.documentElement.setAttribute("data-bs-theme", next);
      localStorage.setItem("theme", next);
      updateIcon(next);
    });
  }
})();
