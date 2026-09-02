import { fetchWithAuth } from './auth.js';
import { fetchWithTimeout } from './shared.js';
import { loadConfigRowsEls } from './loadconfig.js';

const hwStatusEl = document.getElementById('hw-status');
const hwRowsEl = document.getElementById('hw-rows');
const benchmarkBtn = document.getElementById('benchmark-btn');
const benchmarkStatusEl = document.getElementById('benchmark-status');
const benchCpuEl = document.getElementById('bench-cpu');
const benchMemoryEl = document.getElementById('bench-memory');
const benchDiskEl = document.getElementById('bench-disk');

const recommendNInput = document.getElementById('recommend-n-input');
const recommendMaxEl = document.getElementById('recommend-max');
const recommendMinEl = document.getElementById('recommend-min');

// R_total is needed before a recommendation can be shown; null until "Run
// benchmark" has run at least once.
let cpuNumbersPerSecondValue = null;

// Unrounded ceiling/floor from the last recomputeRecommendation() call - what
// "Set recommended" writes, kept separate from the rounded text shown to the user.
let recommendedMaxValue = null;
let recommendedMinValue = null;

// ceiling(N) = 0.8 * (R_total / N); floor = 0.5 * ceiling. Per-vote fair share
// of the node's total capacity, at 80% of that share.
function recomputeRecommendation() {
  if (cpuNumbersPerSecondValue == null) {
    recommendMaxEl.textContent = '—';
    recommendMinEl.textContent = '—';
    recommendedMaxValue = null;
    recommendedMinValue = null;
    return;
  }

  const n = Number(recommendNInput.value);

  if (!Number.isFinite(n) || n <= 0) {
    recommendMaxEl.textContent = 'Enter N';
    recommendMinEl.textContent = '—';
    recommendedMaxValue = null;
    recommendedMinValue = null;
    return;
  }

  const ceiling = 0.8 * (cpuNumbersPerSecondValue / n);
  const floor = 0.5 * ceiling;

  recommendMaxEl.textContent = Math.round(ceiling).toLocaleString();
  recommendMinEl.textContent = Math.round(floor).toLocaleString();
  recommendedMaxValue = ceiling;
  recommendedMinValue = floor;
}

recommendNInput.addEventListener('input', recomputeRecommendation);

function renderHardwareRows(hw) {
  hwRowsEl.innerHTML = '';

  const rows = [
    ['Environment', hw.environment],
    ['CPU', hw.cpu + ', Logic CPU ' + hw.processorCount],
    ['RAM', hw.totalMemoryMb.toLocaleString() + ' MB'],
    ['Disk', hw.diskTotalGb + ' GB']
  ];

  for (const [label, value] of rows) {
    const row = document.createElement('div');
    row.className = 'bench-row';

    const labelEl = document.createElement('span');
    labelEl.className = 'loadconfig-label';
    labelEl.textContent = label;

    const valueEl = document.createElement('span');
    valueEl.className = 'bench-value';
    valueEl.textContent = value;

    row.appendChild(labelEl);
    row.appendChild(valueEl);
    hwRowsEl.appendChild(row);
  }
}

async function loadHardwareInfo() {
  try {
    const response = await fetchWithTimeout('/api/nodebenchmark/hardware', { cache: 'no-store' }, 5000);
    if (!response.ok) {
      throw new Error('unavailable');
    }
    const hw = await response.json();
    hwStatusEl.textContent = '';
    renderHardwareRows(hw);
  } catch (err) {
    hwStatusEl.textContent = 'Could not load hardware info.';
    hwRowsEl.innerHTML = '';
  }
}

benchmarkBtn.addEventListener('click', async () => {
  benchmarkBtn.disabled = true;
  benchCpuEl.textContent = '—';
  benchMemoryEl.textContent = '—';
  benchDiskEl.textContent = '—';
  cpuNumbersPerSecondValue = null;
  recomputeRecommendation();

  try {
    benchmarkStatusEl.textContent = 'Running benchmark… this takes about 25 seconds.';
    const response = await fetchWithAuth('/api/nodebenchmark/run', { method: 'POST' });

    if (!response.ok) {
      throw new Error('HTTP ' + response.status);
    }

    const result = await response.json();
    benchCpuEl.textContent = Math.round(result.cpuNumbersPerSecond).toLocaleString() + ' SHA-512 iterations/s';
    benchMemoryEl.textContent = result.memoryMbPerSecond.toFixed(1) + ' MB/s';
    benchDiskEl.textContent = result.diskMbPerSecond.toFixed(1) + ' MB/s';
    cpuNumbersPerSecondValue = result.cpuNumbersPerSecond;
    recomputeRecommendation();
    benchmarkStatusEl.textContent = 'Done.';
  } catch (err) {
    benchmarkStatusEl.textContent = 'Benchmark failed (' + err.message + ').';
  } finally {
    benchmarkBtn.disabled = false;
  }
});

const setRecommendedBtn = document.getElementById('set-recommended-btn');

setRecommendedBtn.addEventListener('click', () => {
  if (recommendedMaxValue == null || recommendedMinValue == null) {
    benchmarkStatusEl.textContent = 'Run the benchmark first.';
    return;
  }

  const cpuRow = loadConfigRowsEls.application.querySelector('[data-setting-name="CpuIterationsPerVote"]');
  if (!cpuRow) {
    benchmarkStatusEl.textContent = 'CPU setting not loaded yet - try again in a moment.';
    return;
  }

  cpuRow.querySelector('[data-field="min"]').value = Math.round(recommendedMinValue);
  cpuRow.querySelector('[data-field="max"]').value = Math.round(recommendedMaxValue);
  benchmarkStatusEl.textContent = 'Applied to CPU Min/Max above - click "Save changes" to persist.';
});

loadHardwareInfo();
