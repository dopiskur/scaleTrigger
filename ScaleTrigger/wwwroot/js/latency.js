import { fetchWithTimeout } from './shared.js';

// Only /api/vote/add's own latency, not every route /metrics exports (health checks, the
// dashboard's own polling) - that's the traffic this tool exists to characterize.
const LATENCY_ROUTE = 'api/vote/add';
const HISTOGRAM_METRIC = 'http_server_request_duration_seconds_bucket';

const latencyStatusEl = document.getElementById('latency-status');
const latencyChartEl = document.getElementById('latency-chart');

// Minimal Prometheus text-exposition parser: enough for "metric{labels} value" lines, which
// is all /metrics (OpenTelemetry's Prometheus exporter) ever emits here. No quoted commas or
// escaped quotes appear in this app's label values, so a plain split is safe.
function parsePrometheusText(text) {
  const series = [];

  for (const line of text.split('\n')) {
    if (!line || line.startsWith('#')) {
      continue;
    }

    const match = line.match(/^([a-zA-Z_:][a-zA-Z0-9_:]*)(\{[^}]*\})?\s+(\S+)\s*$/);
    if (!match) {
      continue;
    }

    const [, name, labelsRaw, valueRaw] = match;
    const labels = {};
    if (labelsRaw) {
      for (const pair of labelsRaw.slice(1, -1).split(',')) {
        const eq = pair.indexOf('=');
        if (eq === -1) {
          continue;
        }
        const key = pair.slice(0, eq).trim();
        let value = pair.slice(eq + 1).trim();
        if (value.startsWith('"') && value.endsWith('"')) {
          value = value.slice(1, -1);
        }
        labels[key] = value;
      }
    }

    series.push({ name, labels, value: Number(valueRaw) });
  }

  return series;
}

// Histogram buckets are cumulative per label combination (le="0.05" already includes
// everything <= 0.025, etc.) - multiple series can share an "le" only when they differ in
// some other label (route, status code, protocol...); summing those independent cumulative
// sums per "le" still yields a valid aggregate cumulative histogram for this route.
function aggregateBucketsByLe(series) {
  const buckets = new Map();

  for (const s of series) {
    if (s.name !== HISTOGRAM_METRIC || s.labels.http_route !== LATENCY_ROUTE) {
      continue;
    }
    buckets.set(s.labels.le, (buckets.get(s.labels.le) ?? 0) + s.value);
  }

  return buckets;
}

// Converts cumulative bucket counts into per-bucket counts (requests falling in that specific
// range, not "at or under" it) by taking consecutive differences in ascending "le" order.
function toHistogramRows(buckets) {
  const entries = [...buckets.entries()]
    .map(([le, cumulativeCount]) => ({ le, leValue: le === '+Inf' ? Infinity : Number(le), cumulativeCount }))
    .sort((a, b) => a.leValue - b.leValue);

  let previous = 0;
  return entries.map(({ le, leValue, cumulativeCount }) => {
    const count = cumulativeCount - previous;
    previous = cumulativeCount;
    return { le, leValue, count };
  });
}

function formatBucketLabel(leValue) {
  if (leValue === Infinity) {
    return '>10s'; // the +Inf bucket - already reads as "over the last boundary", no leading "≤"
  }
  return '≤' + (leValue < 1 ? Math.round(leValue * 1000) + 'ms' : leValue.toLocaleString() + 's');
}

function renderHistogram(rows, totalCount) {
  latencyChartEl.innerHTML = '';

  const maxCount = Math.max(1, ...rows.map(r => r.count));

  for (const row of rows) {
    const barRow = document.createElement('div');
    barRow.className = 'latency-row';

    const labelEl = document.createElement('span');
    labelEl.className = 'latency-label';
    labelEl.textContent = formatBucketLabel(row.leValue);

    const barTrack = document.createElement('div');
    barTrack.className = 'latency-bar-track';
    const bar = document.createElement('div');
    bar.className = 'latency-bar';
    bar.style.width = (row.count / maxCount * 100) + '%';
    barTrack.appendChild(bar);

    const countEl = document.createElement('span');
    countEl.className = 'latency-count';
    countEl.textContent = row.count.toLocaleString();

    barRow.appendChild(labelEl);
    barRow.appendChild(barTrack);
    barRow.appendChild(countEl);
    latencyChartEl.appendChild(barRow);
  }

  latencyStatusEl.textContent = totalCount.toLocaleString() + ' requests measured since startup.';
}

let latencyCallId = 0;

async function loadLatencyHistogram() {
  const callId = ++latencyCallId;
  try {
    const response = await fetchWithTimeout('/metrics', { cache: 'no-store' }, 5000);
    if (!response.ok) {
      throw new Error('HTTP ' + response.status);
    }
    const series = parsePrometheusText(await response.text());
    if (callId !== latencyCallId) {
      return;
    }

    const buckets = aggregateBucketsByLe(series);
    if (buckets.size === 0) {
      latencyChartEl.innerHTML = '';
      latencyStatusEl.textContent = 'No POST /api/vote/add requests measured yet.';
      return;
    }

    const rows = toHistogramRows(buckets);
    const totalCount = rows.reduce((sum, r) => sum + r.count, 0);
    renderHistogram(rows, totalCount);
  } catch (err) {
    if (callId !== latencyCallId) {
      return;
    }
    latencyChartEl.innerHTML = '';
    latencyStatusEl.textContent = 'Could not load latency data.';
  }
}

// Paused while the tab is hidden, same as report.js's refresh polling.
let latencyTimer = null;

function startLatencyPolling() {
  if (latencyTimer !== null) {
    return;
  }
  loadLatencyHistogram();
  latencyTimer = setInterval(loadLatencyHistogram, 5000);
}

function stopLatencyPolling() {
  if (latencyTimer === null) {
    return;
  }
  clearInterval(latencyTimer);
  latencyTimer = null;
}

document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    stopLatencyPolling();
  } else {
    startLatencyPolling();
  }
});

if (!document.hidden) {
  startLatencyPolling();
}
