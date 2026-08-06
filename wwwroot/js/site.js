(function () {
  const toggle = document.getElementById("theme-toggle");
  const icon = document.getElementById("theme-icon");

  function updateIcon(theme) {
    if (icon) icon.textContent = theme === "dark" ? "☀️" : "🌙";
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
