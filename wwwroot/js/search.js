const results = document.getElementById("search-results");
const resultsStatus = document.getElementById("results-status");

function setResultsStatus(text, className) {
  if (!resultsStatus) return;
  resultsStatus.textContent = text;
  resultsStatus.className = `small mb-2 ${className}`;
}

function renderResults(cartoons) {
  if (cartoons.length === 0) {
    results.innerHTML = '<p class="text-muted">No matches.</p>';
    return;
  }
  results.innerHTML = CartoonCard.renderList(cartoons);
}

async function loadResults() {
  const params = new URLSearchParams(window.location.search);
  const q = (params.get("q") ?? "").trim();
  if (!q) {
    results.innerHTML = '<p class="text-muted">Enter a query in the top bar to search.</p>';
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
