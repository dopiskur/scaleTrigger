import { fetchWithAuth } from './auth.js';
import { setDbUnavailable, clearDbUnavailableIfShown, DbError, dbErrorMessageFrom, fetchWithTimeout } from './shared.js';
import { loadLoadConfig, loadConfigNeedsRetry } from './loadconfig.js';

const totalEl = document.getElementById('total');
const updatedEl = document.getElementById('updated');
const payloadEl = document.getElementById('payload');
const statusEl = document.getElementById('status');
const resetBtn = document.getElementById('reset-btn');

function formatPayloadSize(bytes) {
  const kb = bytes / 1024;
  if (kb < 1024) {
    return kb.toFixed(2) + ' KB';
  }
  const mb = kb / 1024;
  if (mb < 1024) {
    return mb.toFixed(2) + ' MB';
  }
  return (mb / 1024).toFixed(2) + ' GB';
}

let refreshCallId = 0;

async function refresh() {
  const callId = ++refreshCallId;
  try {
    const response = await fetchWithTimeout('/api/vote/report', { cache: 'no-store' }, 5000);
    if (!response.ok) {
      throw new DbError(await dbErrorMessageFrom(response));
    }
    const report = await response.json();
    if (callId !== refreshCallId) {
      return; // a newer refresh already completed - don't apply a stale result
    }

    const payloadCount = report.payloadCount ?? 0;
    const payloadBytes = report.payloadTotalBytes ?? 0;

    totalEl.textContent = (report.total ?? 0).toLocaleString();
    updatedEl.textContent = new Date().toLocaleTimeString();
    payloadEl.textContent = payloadCount.toLocaleString() + ' rows (' + formatPayloadSize(payloadBytes) + ')';
    statusEl.textContent = (report.total ?? 0) === 0 ? 'No records found.' : '';
    clearDbUnavailableIfShown();

    // LoadConfig only loads once so it doesn't clobber in-progress edits; retry if it failed.
    if (loadConfigNeedsRetry) {
      loadLoadConfig();
    }
  } catch (err) {
    if (callId !== refreshCallId) {
      return;
    }
    statusEl.textContent = '';
    setDbUnavailable(err instanceof DbError ? err.message : 'Database unavailable.');
  }
}

resetBtn.addEventListener('click', async () => {
  if (!confirm('This drops the entire database schema (all vote, payload and configuration data), shrinks the database and log files, then reinitializes everything from scratch. Continue?')) {
    return;
  }

  try {
    // Only prompts for login if the API comes back 401.
    statusEl.textContent = 'Resetting database…';
    const resetResponse = await fetchWithAuth('/api/vote/reset', { method: 'POST' });

    if (!resetResponse.ok) {
      throw new Error('reset failed (HTTP ' + resetResponse.status + ')');
    }

    statusEl.textContent = 'Database reset.';
    await refresh();
    await loadLoadConfig();
  } catch (err) {
    statusEl.textContent = 'Reset failed (' + err.message + ').';
  }
});

const discardBacklogBtn = document.getElementById('discard-backlog-btn');
const DISCARD_BACKLOG_PAUSE_MS = 3000;

// Toggles LoadEnabled off then back on: while off, VoteAdd is a fast no-op (no
// simulation, no DB write), so whatever's already queued drains near-instantly
// instead of running its full configured cost.
async function setLoadEnabled(enabled) {
  const body = JSON.stringify([{ settingName: 'LoadEnabled', min: enabled ? 1 : 0, max: enabled ? 1 : 0 }]);
  const response = await fetchWithAuth('/api/loadconfig', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body
  });

  if (!response.ok) {
    throw new Error('HTTP ' + response.status);
  }
}

discardBacklogBtn.addEventListener('click', async () => {
  discardBacklogBtn.disabled = true;
  try {
    statusEl.textContent = 'Discarding backlog…';
    await setLoadEnabled(false);

    await new Promise(resolve => setTimeout(resolve, DISCARD_BACKLOG_PAUSE_MS));

    await setLoadEnabled(true);
    statusEl.textContent = 'Backlog discarded.';
    await loadLoadConfig();
  } catch (err) {
    statusEl.textContent = 'Discard failed (' + err.message + ').';
  } finally {
    discardBacklogBtn.disabled = false;
  }
});

// Paused while the tab is hidden so idle dashboards don't add their own request traffic.
let refreshTimer = null;

function startRefreshPolling() {
  if (refreshTimer !== null) {
    return;
  }
  refresh();
  refreshTimer = setInterval(refresh, 4000);
}

function stopRefreshPolling() {
  if (refreshTimer === null) {
    return;
  }
  clearInterval(refreshTimer);
  refreshTimer = null;
}

document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    stopRefreshPolling();
  } else {
    startRefreshPolling();
  }
});

if (!document.hidden) {
  startRefreshPolling();
}
