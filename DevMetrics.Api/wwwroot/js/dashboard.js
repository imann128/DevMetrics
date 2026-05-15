/**
 * DevMetrics Dashboard — client-side logic
 *
 * Responsibilities:
 *  1. Initialise Chart.js with server-rendered data (zero extra HTTP calls on load)
 *  2. Connect to the SignalR /dashboardHub and maintain a resilient connection
 *  3. React to server push events: ScanCompleted, DashboardUpdated
 *  4. Provide refreshDashboard() and loadRepositories() for manual + event-driven updates
 *  5. Handle manual-scan trigger with optimistic UI feedback
 *  6. Delete repository rows with confirmation
 *
 * Depends on: Bootstrap 5, Chart.js 4.4+, chartjs-adapter-date-fns, @microsoft/signalr
 * All loaded via CDN in _Layout.cshtml before this script runs.
 */

/* ── 1. Constants & state ───────────────────────────────────────────────── */

const API  = window.DM.apiBase;      // '' for same-origin
const HUB  = window.DM.hubUrl;       // '/dashboardHub'

/** UTC+05:00 offset in milliseconds (Pakistan Standard Time, no DST). */
const PKT_OFFSET_MS = 5 * 60 * 60 * 1000;

/**
 * Formats a UTC date as a PKT (UTC+05:00) string.
 *
 * @param {Date|string} date  - A Date object or ISO-8601 UTC string.
 * @param {'datetime'|'date'} [mode='datetime']
 *   - 'datetime' → "yyyy-MM-dd HH:mm PKT"
 *   - 'date'     → "yyyy-MM-dd"
 * @returns {string}
 */
function formatPKT(date, mode = 'datetime') {
  const d   = date instanceof Date ? date : new Date(date);
  const pkt = new Date(d.getTime() + PKT_OFFSET_MS);
  const iso = pkt.toISOString();          // always UTC after the offset shift
  if (mode === 'date') return iso.substring(0, 10);
  return iso.slice(0, 16).replace('T', ' ') + ' PKT';
}

let chart        = null;             // Chart.js instance
let hubConnection = null;            // SignalR HubConnection
let currentDays  = 14;               // chart window (changed by <select>)

/* ── 2. DOM references (queried once at startup) ────────────────────────── */

const elConnectionBadge = document.getElementById('connection-badge');
const elConnectionText  = document.getElementById('connection-text');
const elLastScanTime    = document.getElementById('last-scan-time');
const elBtnScan         = document.getElementById('btn-trigger-scan');
const elScanSpinner     = document.getElementById('scan-spinner');
const elChartLoading    = document.getElementById('chart-loading');
const elChartEmpty      = document.getElementById('chart-empty');
const elRepoTbody       = document.getElementById('repo-tbody');
const elRepoTableWrap   = document.getElementById('repo-table-wrap');
const elRepoEmpty       = document.getElementById('repo-empty');
const elRepoLoading     = document.getElementById('repo-loading');
const elRepoCountBadge  = document.getElementById('repo-count-badge');
const elStatRepos       = document.getElementById('stat-repos');
const elStatCommits     = document.getElementById('stat-commits');
const elStatAdded       = document.getElementById('stat-added');
const elStatDeleted     = document.getElementById('stat-deleted');
const elChartDays       = document.getElementById('chart-days');
const elAddRepoForm     = document.getElementById('add-repo-form');
const elAddRepoSpinner  = document.getElementById('add-repo-spinner');

/* ── 3. Chart initialisation ────────────────────────────────────────────── */

/**
 * Builds Chart.js datasets from an array of CommitSummaryDto objects.
 * @param {Array} summaries - [{date, totalCommits, linesAdded, linesDeleted}, ...]
 * @returns {{ labels: string[], datasets: object[] }}
 */
function buildChartData(summaries) {
  const labels = summaries.map(s => formatPKT(s.date, 'date'));  // 'yyyy-MM-dd'
  return {
    labels,
    datasets: [
      {
        type:            'line',
        label:           'Commits',
        data:            summaries.map(s => s.totalCommits),
        borderColor:     'rgba(99, 102, 241, 1)',
        backgroundColor: 'rgba(99, 102, 241, 0.15)',
        pointBackgroundColor: 'rgba(99, 102, 241, 1)',
        borderWidth:     2,
        pointRadius:     4,
        pointHoverRadius: 6,
        tension:         0.35,
        fill:            true,
        yAxisID:         'yCommits',
        order:           1,
      },
      {
        type:            'bar',
        label:           'Lines added',
        data:            summaries.map(s => s.linesAdded),
        backgroundColor: 'rgba(34, 197, 94, 0.55)',
        borderColor:     'rgba(34, 197, 94, 0.8)',
        borderWidth:     1,
        borderRadius:    3,
        yAxisID:         'yLines',
        order:           2,
      },
      {
        type:            'bar',
        label:           'Lines deleted',
        data:            summaries.map(s => -s.linesDeleted),   // negate for visual symmetry
        backgroundColor: 'rgba(239, 68, 68, 0.55)',
        borderColor:     'rgba(239, 68, 68, 0.8)',
        borderWidth:     1,
        borderRadius:    3,
        yAxisID:         'yLines',
        order:           3,
      },
    ]
  };
}

/**
 * Creates (or recreates) the Chart.js instance on #activity-chart.
 * Called once at startup and whenever the canvas needs to be rebuilt.
 * @param {Array} summaries
 */
function initChart(summaries) {
  const canvas = document.getElementById('activity-chart');
  if (!canvas) return;

  if (chart) {
    chart.destroy();
    chart = null;
  }

  const isEmpty = !summaries || summaries.length === 0;
  canvas.classList.toggle('d-none', isEmpty);
  elChartEmpty.classList.toggle('d-none', !isEmpty);

  if (isEmpty) return;

  const isDark = document.documentElement.getAttribute('data-bs-theme') === 'dark';
  const gridColor  = isDark ? 'rgba(255,255,255,.1)'  : 'rgba(0,0,0,.07)';
  const tickColor  = isDark ? 'rgba(255,255,255,.55)' : 'rgba(0,0,0,.5)';

  chart = new Chart(canvas, {
    data: buildChartData(summaries),
    options: {
      responsive:          true,
      maintainAspectRatio: false,
      interaction:         { mode: 'index', intersect: false },
      plugins: {
        legend: {
          position: 'top',
          labels:   { color: tickColor, usePointStyle: true, pointStyleWidth: 12 }
        },
        tooltip: {
          callbacks: {
            label: (ctx) => {
              const val = Math.abs(ctx.parsed.y);
              if (ctx.dataset.label === 'Lines deleted') return ` Lines deleted: ${val}`;
              return ` ${ctx.dataset.label}: ${val}`;
            }
          }
        }
      },
      scales: {
        x: {
          ticks:  { color: tickColor, maxRotation: 45 },
          grid:   { color: gridColor },
        },
        yCommits: {
          type:     'linear',
          position: 'left',
          title:    { display: true, text: 'Commits', color: tickColor },
          ticks:    { color: tickColor, precision: 0 },
          grid:     { color: gridColor },
          min:      0,
        },
        yLines: {
          type:     'linear',
          position: 'right',
          title:    { display: true, text: 'Lines Δ', color: tickColor },
          ticks:    { color: tickColor, precision: 0 },
          grid:     { drawOnChartArea: false },
        },
      }
    }
  });
}

/**
 * Updates the existing Chart.js instance in-place without a full rebuild.
 * @param {Array} summaries
 */
function updateChart(summaries) {
  const isEmpty = !summaries || summaries.length === 0;

  const canvas = document.getElementById('activity-chart');
  canvas.classList.toggle('d-none', isEmpty);
  elChartEmpty.classList.toggle('d-none', !isEmpty);

  if (isEmpty) { if (chart) { chart.destroy(); chart = null; } return; }

  if (!chart) { initChart(summaries); return; }

  const { labels, datasets } = buildChartData(summaries);
  chart.data.labels = labels;
  datasets.forEach((ds, i) => {
    if (chart.data.datasets[i]) {
      chart.data.datasets[i].data = ds.data;
    }
  });
  chart.update('active');   // 'active' uses animation; use 'none' for instant
}

/* ── 4. API helpers ─────────────────────────────────────────────────────── */

/**
 * Typed fetch wrapper — throws on non-2xx.
 * @template T
 * @param {string}  url
 * @param {object}  [options]
 * @returns {Promise<T>}
 */
async function apiFetch(url, options = {}) {
  const response = await fetch(`${API}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options.headers },
    ...options
  });
  if (!response.ok) {
    let detail = response.statusText;
    try {
      const err = await response.json();
      detail = err.detail || err.title || detail;
    } catch { /* ignore parse errors */ }
    throw new Error(`${response.status}: ${detail}`);
  }
  return response.status === 204 ? null : response.json();
}

/* ── 5. Dashboard refresh ───────────────────────────────────────────────── */

/**
 * Fetches fresh dashboard data and updates the chart + stat cards.
 * @param {number} [days] - Override currentDays when called with a specific window.
 */
async function refreshDashboard(days = currentDays) {
  elChartLoading?.classList.remove('d-none');

  try {
    /** @type {{ repositories: any[], dailySummaries: any[], lastScanTime: string|null }} */
    const data = await apiFetch(`/api/dashboard/summary?days=${days}`);

    updateChart(data.dailySummaries);
    updateStatCards(data);

    if (data.lastScanTime) {
      elLastScanTime.textContent = formatPKT(data.lastScanTime);
    }
  } catch (err) {
    console.error('[DevMetrics] refreshDashboard failed:', err);
    showToast('Could not refresh chart data. See console for details.', 'danger');
  } finally {
    elChartLoading?.classList.add('d-none');
  }
}

/**
 * Fetches the repository list and rebuilds the table.
 */
async function loadRepositories() {
  elRepoLoading?.classList.remove('d-none');
  elRepoTableWrap?.classList.add('d-none');
  elRepoEmpty?.classList.add('d-none');

  try {
    /** @type {Array<{id:string, path:string, name:string, lastScannedUtc:string}>} */
    const repos = await apiFetch('/api/repositories');

    renderRepoTable(repos);
    elStatRepos.textContent = repos.length;
    elRepoCountBadge.textContent = repos.length;
  } catch (err) {
    console.error('[DevMetrics] loadRepositories failed:', err);
    showToast('Could not load repository list.', 'danger');
  } finally {
    elRepoLoading?.classList.add('d-none');
  }
}

/* ── 6. Repository table rendering ─────────────────────────────────────── */

/**
 * Rebuilds the repository <tbody> from a fresh list.
 * @param {Array} repos
 */
function renderRepoTable(repos) {
  if (!elRepoTbody) return;

  const empty = !repos || repos.length === 0;
  elRepoTableWrap.classList.toggle('d-none',  empty);
  elRepoEmpty.classList.toggle('d-none',     !empty);

  if (empty) { elRepoTbody.innerHTML = ''; return; }

  const now = Date.now();

  elRepoTbody.innerHTML = repos.map(repo => {
    const scannedAt = new Date(repo.lastScannedUtc);
    const isEpoch   = scannedAt.getFullYear() === 1970;
    const isStale   = isEpoch || (now - scannedAt.getTime()) > 2 * 3_600_000;
    const label     = isEpoch ? 'Never' : formatPKT(scannedAt);
    const icon      = isStale ? 'bi-clock-history' : 'bi-check-circle-fill';
    const cls       = isStale ? 'stale'             : 'fresh';

    return `
      <tr data-repo-id="${esc(repo.id)}">
        <td><span class="fw-medium">${esc(repo.name)}</span></td>
        <td class="d-none d-md-table-cell">
          <span class="repo-path text-body-secondary" title="${esc(repo.path)}">${esc(repo.path)}</span>
        </td>
        <td>
          <span class="${cls}">
            <i class="bi ${icon} me-1"></i>${esc(label)}
          </span>
        </td>
        <td class="text-end">
          <button class="btn btn-sm btn-outline-danger btn-delete-repo"
                  data-repo-id="${esc(repo.id)}"
                  data-repo-name="${esc(repo.name)}"
                  title="Remove ${esc(repo.name)}">
            <i class="bi bi-trash3"></i>
          </button>
        </td>
      </tr>`;
  }).join('');
}

/** HTML-escapes a string to prevent XSS when injecting into innerHTML. */
function esc(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/* ── 7. Stat cards ──────────────────────────────────────────────────────── */

function updateStatCards(data) {
  const summaries = data.dailySummaries || [];
  const total     = n => summaries.reduce((a, s) => a + (s[n] || 0), 0);

  elStatCommits.textContent  =  total('totalCommits');
  elStatAdded.textContent    = '+' + total('linesAdded');
  elStatDeleted.textContent  = '-' + total('linesDeleted');
}

/* ── 8. Toast notifications ─────────────────────────────────────────────── */

/**
 * Displays a Bootstrap toast in the top-right corner.
 * @param {string} message   - HTML-safe message text.
 * @param {'success'|'info'|'warning'|'danger'} [variant]
 * @param {number} [delay]   - Auto-hide delay in ms (0 = sticky).
 */
function showToast(message, variant = 'info', delay = 5000) {
  const container = document.getElementById('toast-container');
  if (!container) return;

  const icons = {
    success: 'bi-check-circle-fill text-success',
    info:    'bi-info-circle-fill text-info',
    warning: 'bi-exclamation-triangle-fill text-warning',
    danger:  'bi-x-circle-fill text-danger',
  };

  const id  = `toast-${Date.now()}`;
  const html = `
    <div id="${id}" class="toast align-items-center border-0 shadow" role="alert"
         aria-live="assertive" aria-atomic="true">
      <div class="d-flex">
        <div class="toast-body d-flex align-items-start gap-2">
          <i class="bi ${icons[variant] ?? icons.info} flex-shrink-0 mt-1"></i>
          <span>${message}</span>
        </div>
        <button type="button" class="btn-close btn-close me-2 m-auto"
                data-bs-dismiss="toast" aria-label="Close"></button>
      </div>
    </div>`;

  container.insertAdjacentHTML('beforeend', html);

  const toastEl = document.getElementById(id);
  const toast   = new bootstrap.Toast(toastEl, {
    autohide: delay > 0,
    delay
  });

  toastEl.addEventListener('hidden.bs.toast', () => toastEl.remove());
  toast.show();
}

/* ── 9. SignalR connection ──────────────────────────────────────────────── */

function setConnectionStatus(state) {
  // state: 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
  const cfg = {
    connecting:   { cls: '',             text: 'Connecting…'   },
    connected:    { cls: 'connected',    text: 'Live'          },
    reconnecting: { cls: 'reconnecting', text: 'Reconnecting…' },
    disconnected: { cls: 'disconnected', text: 'Disconnected'  },
  };

  const { cls, text } = cfg[state] ?? cfg.disconnected;

  elConnectionBadge.className =
    `badge rounded-pill d-flex align-items-center gap-1 ${cls}`;
  elConnectionText.textContent = text;
}

async function startSignalR() {
  hubConnection = new signalR.HubConnectionBuilder()
    .withUrl(HUB)
    .withAutomaticReconnect({
      // Custom back-off: 0s, 2s, 5s, 10s, 30s, then 30s indefinitely
      nextRetryDelayInMilliseconds: (ctx) => {
        const delays = [0, 2000, 5000, 10000, 30000];
        return delays[ctx.previousRetryCount] ?? 30000;
      }
    })
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  // ── Event listeners ──────────────────────────────────────────────────────

  /**
   * ScanCompleted — lightweight notification from SignalRScanNotifier.
   * Payload: { repositoriesScanned, newCommitsFound, durationMs, status }
   */
  hubConnection.on('ScanCompleted', (result) => {
    console.log('[DevMetrics] ScanCompleted', result);

    const verb    = result.newCommitsFound === 1 ? 'commit' : 'commits';
    const variant = result.status === 'Completed' ? 'success'
                  : result.status === 'Failed'    ? 'danger'
                  :                                 'warning';
    const msg = result.newCommitsFound > 0
      ? `Scan complete — <strong>${result.newCommitsFound}</strong> new ${verb} found`
      : `Scan complete — no new commits`;

    showToast(msg, variant);

    // Update stat cards and chart after scan
    refreshDashboard();
  });

  /**
   * DashboardUpdated — full payload pushed by ScanController after a manual trigger.
   * Payload: DashboardDataDto
   */
  hubConnection.on('DashboardUpdated', (data) => {
    console.log('[DevMetrics] DashboardUpdated', data);

    updateChart(data.dailySummaries);
    updateStatCards(data);
    renderRepoTable(data.repositories);

    elStatRepos.textContent      = data.repositories.length;
    elRepoCountBadge.textContent = data.repositories.length;

    if (data.lastScanTime) {
      elLastScanTime.textContent = formatPKT(data.lastScanTime);
    }
  });

  // ── Connection lifecycle ─────────────────────────────────────────────────

  hubConnection.onreconnecting(() => {
    setConnectionStatus('reconnecting');
    showToast('Connection lost — attempting to reconnect…', 'warning', 0);
  });

  hubConnection.onreconnected(async (connectionId) => {
    setConnectionStatus('connected');
    showToast('Reconnected to server.', 'success', 3000);
    // Re-join the group in case the server lost state
    await hubConnection.invoke('JoinDashboardGroup', 'dashboard').catch(console.warn);
    // Refresh in case we missed updates while disconnected
    await refreshDashboard();
    await loadRepositories();
  });

  hubConnection.onclose(() => {
    setConnectionStatus('disconnected');
    showToast(
      'Connection closed. Reload the page or check server status.',
      'danger',
      0
    );
  });

  // ── Start ────────────────────────────────────────────────────────────────

  setConnectionStatus('connecting');

  try {
    await hubConnection.start();
    await hubConnection.invoke('JoinDashboardGroup', 'dashboard');
    setConnectionStatus('connected');
    console.log('[DevMetrics] SignalR connected');
  } catch (err) {
    setConnectionStatus('disconnected');
    console.error('[DevMetrics] SignalR failed to start:', err);
    showToast('Could not connect to real-time updates. Charts will not auto-refresh.', 'warning', 0);
  }
}

/* ── 10. Manual scan trigger ────────────────────────────────────────────── */

async function triggerScan() {
  elBtnScan.disabled = true;
  elScanSpinner.classList.remove('d-none');
  elBtnScan.querySelector('span:not(.spinner-border)').textContent = 'Scanning…';

  try {
    const response = await apiFetch('/api/scan/trigger', { method: 'POST' });

    showToast(
      `Scan started — <code>${response.operationId.slice(0, 8)}…</code>. ` +
      `Charts will update automatically.`,
      'info',
      4000
    );

    // Poll the operation status to update the button label
    pollScanStatus(response.operationId);
  } catch (err) {
    console.error('[DevMetrics] triggerScan failed:', err);
    showToast(`Failed to trigger scan: ${err.message}`, 'danger');
    resetScanButton();
  }
}

async function pollScanStatus(operationId) {
  const maxPolls = 60;  // up to 60 × 2s = 2 minutes
  let polls = 0;

  const interval = setInterval(async () => {
    polls++;
    if (polls > maxPolls) {
      clearInterval(interval);
      resetScanButton();
      return;
    }

    try {
      const op = await apiFetch(`/api/scan/status/${operationId}`);

      if (op.status === 'Completed' || op.status === 'Failed') {
        clearInterval(interval);
        resetScanButton();
        if (op.status === 'Failed') {
          showToast(`Scan failed: ${op.error ?? 'unknown error'}`, 'danger');
        }
      }
    } catch { clearInterval(interval); resetScanButton(); }
  }, 2000);
}

function resetScanButton() {
  elBtnScan.disabled = false;
  elScanSpinner.classList.add('d-none');
  elBtnScan.querySelector('span:not(.spinner-border)').textContent = 'Scan now';
}

/* ── 11. Delete repository ──────────────────────────────────────────────── */

async function deleteRepository(id, name) {
  if (!confirm(`Remove "${name}" from tracking?\n\nThis will delete all stored commits and daily summaries for this repository. The files on disk are not affected.`)) {
    return;
  }

  try {
    await apiFetch(`/api/repositories/${id}`, { method: 'DELETE' });
    showToast(`'${name}' removed successfully.`, 'success', 3000);
    await loadRepositories();
    await refreshDashboard();
  } catch (err) {
    console.error('[DevMetrics] deleteRepository failed:', err);
    showToast(`Could not remove repository: ${err.message}`, 'danger');
  }
}

/* ── 12. Add-repo form — show spinner ───────────────────────────────────── */

function wireAddRepoForm() {
  if (!elAddRepoForm) return;

  elAddRepoForm.addEventListener('submit', () => {
    const input = document.getElementById('RepoPath');
    if (!input?.value.trim()) return;   // let HTML5 validation handle empty

    elAddRepoSpinner?.classList.remove('d-none');
    const btn = elAddRepoForm.querySelector('button[type="submit"]');
    if (btn) btn.disabled = true;
  });
}

/* ── 13. Theme change — rebuild chart with new grid colours ─────────────── */

function watchTheme() {
  const observer = new MutationObserver((mutations) => {
    for (const m of mutations) {
      if (m.attributeName === 'data-bs-theme' && chart) {
        // Re-init chart so grid/tick colours update
        const summaries = chart.data.labels.map((label, i) => ({
          date:          label,
          totalCommits:  chart.data.datasets[0].data[i],
          linesAdded:    chart.data.datasets[1].data[i],
          linesDeleted: -chart.data.datasets[2].data[i],
        }));
        initChart(summaries);
      }
    }
  });
  observer.observe(document.documentElement, { attributes: true });
}

/* ── 14. Event delegation for delete buttons ────────────────────────────── */

function wireDeleteButtons() {
  // Use event delegation so dynamically-rendered rows are handled too.
  document.addEventListener('click', (e) => {
    const btn = e.target.closest('.btn-delete-repo');
    if (!btn) return;

    const id   = btn.dataset.repoId;
    const name = btn.dataset.repoName;
    if (id && name) deleteRepository(id, name);
  });
}

/* ── 15. Chart day-window selector ─────────────────────────────────────── */

function wireChartDaysSelector() {
  elChartDays?.addEventListener('change', (e) => {
    currentDays = parseInt(e.target.value, 10);
    refreshDashboard(currentDays);
  });
}

/* ── 16. Initialise everything on DOM ready ─────────────────────────────── */

document.addEventListener('DOMContentLoaded', async () => {
  console.log('[DevMetrics] Dashboard initialising…');

  // Chart — use server-rendered data for zero-latency first paint
  initChart(window.DM.initialSummaries);

  // Wire up interactive elements
  elBtnScan?.addEventListener('click', triggerScan);
  wireAddRepoForm();
  wireDeleteButtons();
  wireChartDaysSelector();
  watchTheme();

  // SignalR — connect asynchronously so it doesn't block the UI
  await startSignalR();

  console.log('[DevMetrics] Dashboard ready');
});