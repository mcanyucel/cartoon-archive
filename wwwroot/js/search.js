const results = document.getElementById("search-results");
const resultsStatus = document.getElementById("results-status");

function setResultsStatus(text, className) {
  if (!resultsStatus) return;
  resultsStatus.textContent = text;
  resultsStatus.className = `small mb-2 ${className}`;
}

function renderResults(cartoons) {
  const count = document.getElementById("results-count");
  if (count)
    count.textContent =
      cartoons.length === 0
        ? "no matches"
        : `${cartoons.length} match${cartoons.length === 1 ? "" : "es"}`;

  if (cartoons.length === 0) {
    results.innerHTML =
      '<p class="text-body-secondary">Nothing matched. Try a shorter root — search matches prefixes, so <em>kitap</em> finds <em>kitabı</em> but not the other way round.</p>';
    return;
  }
  results.innerHTML = CartoonCard.renderList(cartoons);
}

async function loadResults() {
  const params = new URLSearchParams(window.location.search);
  const q = (params.get("q") ?? "").trim();
  if (!q) {
    results.innerHTML =
      '<p class="text-body-secondary">Search the captions from the bar above.</p>';
    return;
  }
  try {
    const response = await fetch(`/api/images/search?q=${encodeURIComponent(q)}`);
    if (!response.ok) {
      results.innerHTML = '<p class="text-danger">Search failed.</p>';
      return;
    }
    const cartoons = await response.json();
    renderResults(cartoons);
  } catch (e) {
    results.innerHTML = `<p class="text-danger">Search failed: ${CartoonCard.escapeHtml(e.message)}</p>`;
  }
}

CartoonCard.attachRetryHandler(results, {
  onSuccess: loadResults,
  onError: (msg) => setResultsStatus(msg, "text-danger"),
});
CartoonCard.attachLightboxHandler(results, { onAfterDelete: loadResults });
CartoonCard.attachShareHandler(results, {
  onMessage: (text, className) => setResultsStatus(text, className),
});

loadResults();
