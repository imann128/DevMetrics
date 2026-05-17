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
 *  7. Compute and display streak, trend, peak-day, and active-days derived stats
 *
 * Depends on: Bootstrap 5, Chart.js 4.4+, @microsoft/signalr
 */

/* ── 1. Constants & state ───────────────────────────────────────────────── */

const API = window.DM.apiBase;
const HUB = window.DM.hubUrl;

/** UTC+05:00 offset in milliseconds (Pakistan Standard Time). */
const PKT_OFFSET_MS = 5 * 60 * 60 * 1000;

function formatPKT(date, mode = 'datetime') {
  const d   = date instanceof Date ? date : new Date(date);
  const pkt = new Date(d.getTime() + PKT_OFFSET_MS);
  const iso = pkt.toISOString();
  if (mode === 'date') return iso.substring(0, 10);
  return iso.slice(0, 16).replace('T', ' ') + ' PKT';
}

let chart         = null;
let hubConnection = null;
let currentDays   = 14;

/* ── 2. DOM references ──────────────────────────────────────────────────── */

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
const elStatActive      = document.getElementById('stat-active');
const elStatStreak      = document.getElementById('stat-streak');
const elStatPeak        = document.getElementById('stat-peak');
const elStatPeakDate    = document.getElementById('stat-peak-date');
const elTrendCommits    = document.getElementById('trend-commits');
const elTrendAdded      = document.getElementById('trend-added');
const elTrendDeleted    = document.getElementById('trend-deleted');
const elHlStreak        = document.getElementById('hl-streak');
const elHlPeak          = document.getElementById('hl-peak');
const elHlPeakDate      = document.getElementById('hl-peak-date');
const elHlActive        = document.getElementById('hl-active');
const elChartDays       = document.getElementById('chart-days');
const elAddRepoForm     = document.getElementById('add-repo-form');
const elAddRepoSpinner  = document.getElementById('add-repo-spinner');

/* ── 3. Derived-stat calculations ───────────────────────────────────────── */

/**
 * Counts consecutive days (from the most recent date backwards) that
 * have at least one commit. Assumes the summaries window may not include
 * today; streak resets only if there is a gap with zero commits.
 */
function calcStreak(summaries) {
  if (!summaries || summaries.length === 0) return 0;
  const sorted = [...summaries]
    .sort((a, b) => new Date(b.date) - new Date(a.date));  // newest first
  let streak = 0;
  for (const s of sorted) {
    if ((s.totalCommits || 0) > 0) streak++;
    else break;
  }
  return streak;
}

/**
 * Returns the percentage change between the first half and the second half
 * of the summaries window for a given numeric field.
 * Positive = improving (more in the second / more recent half).
 * Returns null when there is not enough data to compare.
 */
function calcTrend(summaries, field) {
  if (!summaries || summaries.length < 4) return null;
  const sorted = [...summaries]
    .sort((a, b) => new Date(a.date) - new Date(b.date));  // oldest first
  const mid    = Math.floor(sorted.length / 2);
  const older  = sorted.slice(0, mid).reduce((s, d) => s + (d[field] || 0), 0);
  const newer  = sorted.slice(mid).reduce((s, d)  => s + (d[field] || 0), 0);
  if (older === 0) return null;  // can't compute percentage from zero base
  return Math.round(((newer - older) / older) * 100);
}

/**
 * Returns { commits, date } for the day with the most commits.
 */
function calcPeakDay(summaries) {
  if (!summaries || summaries.length === 0) return { commits: 0, date: null };
  return summaries.reduce(
    (best, s) => (s.totalCommits || 0) > best.commits
      ? { commits: s.totalCommits, date: s.date }
      : best,
    { commits: 0, date: null }
  );
}

/**
 * Returns the number of days in the window that had at least one commit.
 */
function calcActiveDays(summaries) {
  if (!summaries) return 0;
  return summaries.filter(s => (s.totalCommits || 0) > 0).length;
}

/* ── 4. Animated counter ────────────────────────────────────────────────── */

/**
 * Smoothly counts an element's displayed number to a new target value.
 * @param {HTMLElement} el
 * @param {number}      target
 * @param {string}      [prefix='']   e.g. '+' or '-'
 * @param {string}      [suffix='']   e.g. ' days'
 */
function countUp(el, target, prefix = '', suffix = '') {
  if (!el) return;
  const raw   = parseInt(el.textContent.replace(/[^0-9]/g, '')) || 0;
  const start = isNaN(raw) ? 0 : raw;
  if (start === target) { el.textContent = `${prefix}${target.toLocaleString()}${suffix}`; return; }

  const duration  = 550;
  const startTime = performance.now();

  const tick = (now) => {
    const t   = Math.min((now - startTime) / duration, 1);
    const ease = 1 - Math.pow(1 - t, 3);   // ease-out cubic
    el.textContent = `${prefix}${Math.round(start + (target - start) * ease).toLocaleString()}${suffix}`;
    if (t < 1) requestAnimationFrame(tick);
  };

  requestAnimationFrame(tick);
}

/* ── 5. Trend badge renderer ────────────────────────────────────────────── */

/**
 * Renders a coloured trend indicator into an element.
 * @param {HTMLElement} el
 * @param {number|null} pct  percentage change (positive = up, negative = down)
 */
function renderTrend(el, pct) {
  if (!el) return;
  if (pct === null || pct === undefined) { el.innerHTML = ''; return; }

  const abs = Math.abs(pct);
  if (abs < 5) {
    el.className = 'dm-trend flat';
    el.innerHTML = `<i class="bi bi-arrow-right"></i> stable`;
    return;
  }

  const up = pct > 0;
  el.className = `dm-trend ${up ? 'up' : 'down'}`;
  el.innerHTML = `<i class="bi bi-arrow-${up ? 'up' : 'down'}-short"></i>${abs}% vs prev`;
}

/* ── 6. Chart ────────────────────────────────────────────────────────────── */

/**
 * Creates a linear gradient for the commits area fill.
 * Falls back to a flat colour if chartArea is not yet available.
 */
function commitsGradient(context) {
  const { ctx, chartArea } = context.chart;
  if (!chartArea) return 'rgba(99,102,241,0.2)';
  const g = ctx.createLinearGradient(0, chartArea.top, 0, chartArea.bottom);
  g.addColorStop(0, 'rgba(99,102,241,0.4)');
  g.addColorStop(1, 'rgba(99,102,241,0.0)');
  return g;
}

function buildChartData(summaries) {
  const labels = summaries.map(s => formatPKT(s.date, 'date'));
  return {
    labels,
    datasets: [
      {
        label:                'Commits',
        data:                 summaries.map(s => s.totalCommits),
        borderColor:          'rgba(99,102,241,1)',
        backgroundColor:      commitsGradient,
        pointBackgroundColor: 'rgba(99,102,241,1)',
        pointBorderColor:     '#fff',
        pointBorderWidth:     2,
        borderWidth:          2.5,
        pointRadius:          4,
        pointHoverRadius:     6,
        tension:              0.4,
        fill:                 true,
      },
    ]
  };
}

function initChart(summaries) {
  const canvas = document.getElementById('activity-chart');
  if (!canvas) return;

  if (chart) { chart.destroy(); chart = null; }

  const isEmpty = !summaries || summaries.length === 0;
  canvas.classList.toggle('d-none', isEmpty);
  elChartEmpty.classList.toggle('d-none', !isEmpty);
  if (isEmpty) return;

  const isDark     = document.documentElement.getAttribute('data-bs-theme') === 'dark';
  const gridColor  = isDark ? 'rgba(255,255,255,.08)' : 'rgba(0,0,0,.06)';
  const tickColor  = isDark ? 'rgba(255,255,255,.5)'  : 'rgba(0,0,0,.45)';

  chart = new Chart(canvas, {
    data: buildChartData(summaries),
    options: {
      responsive:          true,
      maintainAspectRatio: false,
      interaction:         { mode: 'index', intersect: false },
      plugins: {
        legend: {
          position: 'top',
          labels:   {
            color:           tickColor,
            usePointStyle:   true,
            pointStyleWidth: 12,
            padding:         16,
          }
        },
        tooltip: {
          backgroundColor: isDark ? 'rgba(30,30,40,.92)' : 'rgba(255,255,255,.96)',
          titleColor:      isDark ? '#e5e7eb' : '#111827',
          bodyColor:       isDark ? '#d1d5db' : '#374151',
          borderColor:     isDark ? 'rgba(255,255,255,.1)' : 'rgba(0,0,0,.08)',
          borderWidth:     1,
          padding:         10,
          callbacks: {
            label: (ctx) => `  Commits: ${ctx.parsed.y.toLocaleString()}`
          }
        }
      },
      scales: {
        x: {
          ticks: { color: tickColor, maxRotation: 45, font: { size: 11 } },
          grid:  { color: gridColor },
        },
        y: {
          ticks: { color: tickColor, precision: 0, font: { size: 11 } },
          grid:  { color: gridColor },
          min:   0,
        },
      }
    }
  });
}

function updateChart(summaries) {
  const isEmpty = !summaries || summaries.length === 0;
  const canvas  = document.getElementById('activity-chart');
  canvas.classList.toggle('d-none', isEmpty);
  elChartEmpty.classList.toggle('d-none', !isEmpty);

  if (isEmpty) { if (chart) { chart.destroy(); chart = null; } return; }
  if (!chart)  { initChart(summaries); return; }

  const { labels, datasets } = buildChartData(summaries);
  chart.data.labels = labels;
  datasets.forEach((ds, i) => {
    if (chart.data.datasets[i]) chart.data.datasets[i].data = ds.data;
  });
  chart.update('active');
}

/* ── 7. Stat cards ───────────────────────────────────────────────────────── */

function updateStatCards(data) {
  const summaries = data.dailySummaries || [];

  const totalCommits  = summaries.reduce((s, d) => s + (d.totalCommits  || 0), 0);
  const totalAdded    = summaries.reduce((s, d) => s + (d.linesAdded    || 0), 0);
  const totalDeleted  = summaries.reduce((s, d) => s + (d.linesDeleted  || 0), 0);

  const streak     = calcStreak(summaries);
  const activeDays = calcActiveDays(summaries);
  const peak       = calcPeakDay(summaries);

  const trendCommits  = calcTrend(summaries, 'totalCommits');
  const trendAdded    = calcTrend(summaries, 'linesAdded');
  const trendDeleted  = calcTrend(summaries, 'linesDeleted');

  // Animated counters
  countUp(elStatCommits, totalCommits);
  countUp(elStatAdded,   totalAdded,   '+');
  countUp(elStatDeleted, totalDeleted, '-');
  countUp(elStatActive,  activeDays,  '',  ` / ${currentDays}`);
  countUp(elStatStreak,  streak,      '',  streak === 1 ? ' day' : ' days');
  countUp(elStatPeak,    peak.commits);

  if (elStatPeakDate) {
    elStatPeakDate.textContent = peak.date
      ? `commits on ${formatPKT(peak.date, 'date')}`
      : 'commits in one day';
  }

  // Trend badges
  renderTrend(elTrendCommits,  trendCommits);
  renderTrend(elTrendAdded,    trendAdded);
  renderTrend(elTrendDeleted,  trendDeleted);

  // Highlights strip (below chart)
  if (elHlStreak)    elHlStreak.textContent  = streak;
  if (elHlPeak)      elHlPeak.textContent    = peak.commits.toLocaleString();
  if (elHlPeakDate)  elHlPeakDate.textContent = peak.date
    ? formatPKT(peak.date, 'date')
    : '—';
  if (elHlActive) elHlActive.textContent = activeDays;
}

/* ── 8. API helpers ─────────────────────────────────────────────────────── */

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
    } catch { /* ignore */ }
    throw new Error(`${response.status}: ${detail}`);
  }
  return response.status === 204 ? null : response.json();
}

/* ── 9. Dashboard refresh ───────────────────────────────────────────────── */

async function refreshDashboard(days = currentDays) {
  elChartLoading?.classList.remove('d-none');
  try {
    const data = await apiFetch(`/api/dashboard/summary?days=${days}`);
    updateChart(data.dailySummaries);
    updateStatCards(data);
    if (data.lastScanTime) elLastScanTime.textContent = formatPKT(data.lastScanTime);
  } catch (err) {
    console.error('[DevMetrics] refreshDashboard failed:', err);
    showToast('Could not refresh chart data.', 'danger');
  } finally {
    elChartLoading?.classList.add('d-none');
  }
}

async function loadRepositories() {
  elRepoLoading?.classList.remove('d-none');
  elRepoTableWrap?.classList.add('d-none');
  elRepoEmpty?.classList.add('d-none');
  try {
    const repos = await apiFetch('/api/repositories');
    renderRepoTable(repos);
    const count = repos.length;
    if (elStatRepos)       elStatRepos.textContent       = count;
    if (elRepoCountBadge)  elRepoCountBadge.textContent  = count;
  } catch (err) {
    console.error('[DevMetrics] loadRepositories failed:', err);
    showToast('Could not load repository list.', 'danger');
  } finally {
    elRepoLoading?.classList.add('d-none');
  }
}

/* ── 10. Repository table ───────────────────────────────────────────────── */

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
    const icon      = isStale ? 'bi-clock-history'    : 'bi-check-circle-fill';
    const cls       = isStale ? 'stale'               : 'fresh';

    return `
      <tr data-repo-id="${esc(repo.id)}">
        <td><span class="fw-medium">${esc(repo.name)}</span></td>
        <td class="d-none d-md-table-cell">
          <span class="repo-path text-body-secondary"
                title="${esc(repo.path)}">${esc(repo.path)}</span>
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

function esc(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/* ── 11. Toast notifications ────────────────────────────────────────────── */

function showToast(message, variant = 'info', delay = 5000) {
  const container = document.getElementById('toast-container');
  if (!container) return;

  const icons = {
    success: 'bi-check-circle-fill text-success',
    info:    'bi-info-circle-fill text-info',
    warning: 'bi-exclamation-triangle-fill text-warning',
    danger:  'bi-x-circle-fill text-danger',
  };

  const id   = `toast-${Date.now()}`;
  const html = `
    <div id="${id}" class="toast align-items-center border-0 shadow" role="alert"
         aria-live="assertive" aria-atomic="true">
      <div class="d-flex">
        <div class="toast-body d-flex align-items-start gap-2">
          <i class="bi ${icons[variant] ?? icons.info} flex-shrink-0 mt-1"></i>
          <span>${message}</span>
        </div>
        <button type="button" class="btn-close me-2 m-auto"
                data-bs-dismiss="toast" aria-label="Close"></button>
      </div>
    </div>`;

  container.insertAdjacentHTML('beforeend', html);
  const toastEl = document.getElementById(id);
  const toast   = new bootstrap.Toast(toastEl, { autohide: delay > 0, delay });
  toastEl.addEventListener('hidden.bs.toast', () => toastEl.remove());
  toast.show();
}

/* ── 12. SignalR ─────────────────────────────────────────────────────────── */

function setConnectionStatus(state) {
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
      nextRetryDelayInMilliseconds: (ctx) => {
        const delays = [0, 2000, 5000, 10000, 30000];
        return delays[ctx.previousRetryCount] ?? 30000;
      }
    })
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  hubConnection.on('ScanCompleted', (result) => {
    const verb    = result.newCommitsFound === 1 ? 'commit' : 'commits';
    const variant = result.status === 'Completed' ? 'success'
                  : result.status === 'Failed'    ? 'danger' : 'warning';
    const msg = result.newCommitsFound > 0
      ? `Scan complete — <strong>${result.newCommitsFound}</strong> new ${verb} found`
      : `Scan complete — no new commits`;
    showToast(msg, variant);
    refreshDashboard();
  });

  hubConnection.on('DashboardUpdated', (data) => {
    updateChart(data.dailySummaries);
    updateStatCards(data);
    renderRepoTable(data.repositories);
    if (elStatRepos)       elStatRepos.textContent      = data.repositories.length;
    if (elRepoCountBadge)  elRepoCountBadge.textContent = data.repositories.length;
    if (data.lastScanTime) elLastScanTime.textContent   = formatPKT(data.lastScanTime);
  });

  hubConnection.onreconnecting(() => {
    setConnectionStatus('reconnecting');
    showToast('Connection lost — attempting to reconnect…', 'warning', 0);
  });

  hubConnection.onreconnected(async () => {
    setConnectionStatus('connected');
    showToast('Reconnected to server.', 'success', 3000);
    await hubConnection.invoke('JoinDashboardGroup', 'dashboard').catch(console.warn);
    await refreshDashboard();
    await loadRepositories();
  });

  hubConnection.onclose(() => {
    setConnectionStatus('disconnected');
    showToast('Connection closed. Reload the page or check server status.', 'danger', 0);
  });

  setConnectionStatus('connecting');
  try {
    await hubConnection.start();
    await hubConnection.invoke('JoinDashboardGroup', 'dashboard');
    setConnectionStatus('connected');
  } catch (err) {
    setConnectionStatus('disconnected');
    console.error('[DevMetrics] SignalR failed to start:', err);
    showToast('Could not connect to real-time updates.', 'warning', 0);
  }
}

/* ── 13. Manual scan trigger ────────────────────────────────────────────── */

async function triggerScan() {
  elBtnScan.disabled = true;
  elScanSpinner.classList.remove('d-none');
  elBtnScan.querySelector('span:not(.spinner-border)').textContent = 'Scanning…';
  try {
    const response = await apiFetch('/api/scan/trigger', { method: 'POST' });
    showToast(
      `Scan started — <code>${response.operationId.slice(0, 8)}…</code>. Charts will update automatically.`,
      'info', 4000
    );
    pollScanStatus(response.operationId);
  } catch (err) {
    console.error('[DevMetrics] triggerScan failed:', err);
    showToast(`Failed to trigger scan: ${err.message}`, 'danger');
    resetScanButton();
  }
}

async function pollScanStatus(operationId) {
  const maxPolls = 60;
  let polls = 0;
  const interval = setInterval(async () => {
    if (++polls > maxPolls) { clearInterval(interval); resetScanButton(); return; }
    try {
      const op = await apiFetch(`/api/scan/status/${operationId}`);
      if (op.status === 'Completed' || op.status === 'Failed') {
        clearInterval(interval);
        resetScanButton();
        if (op.status === 'Failed')
          showToast(`Scan failed: ${op.error ?? 'unknown error'}`, 'danger');
      }
    } catch { clearInterval(interval); resetScanButton(); }
  }, 2000);
}

function resetScanButton() {
  elBtnScan.disabled = false;
  elScanSpinner.classList.add('d-none');
  elBtnScan.querySelector('span:not(.spinner-border)').textContent = 'Scan now';
}

/* ── 14. Delete repository ──────────────────────────────────────────────── */

async function deleteRepository(id, name) {
  if (!confirm(`Remove "${name}" from tracking?\n\nThis will delete all stored commits and daily summaries for this repository. The files on disk are not affected.`))
    return;
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

/* ── 15. Wiring ──────────────────────────────────────────────────────────── */

function wireAddRepoForm() {
  if (!elAddRepoForm) return;
  elAddRepoForm.addEventListener('submit', () => {
    const input = document.getElementById('RepoPath');
    if (!input?.value.trim()) return;
    elAddRepoSpinner?.classList.remove('d-none');
    const btn = elAddRepoForm.querySelector('button[type="submit"]');
    if (btn) btn.disabled = true;
  });
}

function wireDeleteButtons() {
  document.addEventListener('click', (e) => {
    const btn = e.target.closest('.btn-delete-repo');
    if (!btn) return;
    const { repoId: id, repoName: name } = btn.dataset;
    if (id && name) deleteRepository(id, name);
  });
}

function wireChartDaysSelector() {
  elChartDays?.addEventListener('change', (e) => {
    currentDays = parseInt(e.target.value, 10);
    refreshDashboard(currentDays);
  });
}

function watchTheme() {
  new MutationObserver((mutations) => {
    for (const m of mutations) {
      if (m.attributeName === 'data-bs-theme' && chart) {
        // Rebuild chart so grid/tick/tooltip colours update with the theme.
        const summaries = chart.data.labels.map((label, i) => ({
          date:         label,
          totalCommits: chart.data.datasets[0].data[i],
          linesAdded:   0,
          linesDeleted: 0,
        }));
        initChart(summaries);
      }
    }
  }).observe(document.documentElement, { attributes: true });
}

/* ── 16. Bootstrap ───────────────────────────────────────────────────────── */

document.addEventListener('DOMContentLoaded', async () => {
  // Chart — use server-rendered data for zero-latency first paint
  initChart(window.DM.initialSummaries);
  updateStatCards({ dailySummaries: window.DM.initialSummaries,
                    repositories:   window.DM.initialRepositories });

  // Wire interactive elements
  elBtnScan?.addEventListener('click', triggerScan);
  wireAddRepoForm();
  wireDeleteButtons();
  wireChartDaysSelector();
  watchTheme();

  // SignalR — async so it doesn't block the UI
  await startSignalR();
});
