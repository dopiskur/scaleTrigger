import { fetchWithAuth } from './auth.js';
import { setDbUnavailable, clearDbUnavailableIfShown, DbError, dbErrorMessageFrom, fetchWithTimeout } from './shared.js';

const LOAD_CONFIG_LABELS = {
  ConfigRefresh: { label: 'Poll interval', unit: 'seconds', group: 'refresh', single: true, order: 0 },
  LoadEnabled: { label: 'Load generation', group: 'refresh', boolean: true, order: 1 },
  CacheEnabled: { label: 'Cache GET /api/vote/report', group: 'refresh', boolean: true, order: 2 },
  CpuIterationsPerVote: { label: 'CPU', unit: 'SHA-512 iterations', group: 'application', order: 3 },
  MemoryKilobytesPerVote: { label: 'Memory', unit: 'KB', group: 'application', order: 4 },
  DiskWriteKilobytesPerVote: { label: 'Disk write', unit: 'KB', group: 'application', order: 5 },
  NetworkLatencyMillisecondsPerVote: { label: 'Network latency', unit: 'ms', group: 'application', order: 6 },
  PayloadBytesPerVote: { label: 'Payload', unit: 'bytes', group: 'database', order: 7 },
  DbCpuIterationsPerVote: { label: 'CPU', unit: 'SHA-512 iterations', group: 'database', order: 8 }
};

export const loadConfigRowsEls = {
  refresh: document.getElementById('loadconfig-rows-refresh'),
  application: document.getElementById('loadconfig-rows-application'),
  database: document.getElementById('loadconfig-rows-database')
};
const loadConfigSaveBtn = document.getElementById('loadconfig-save-btn');
const loadConfigStatusEl = document.getElementById('loadconfig-status');

// LoadConfig only loads once so it doesn't clobber in-progress edits; report.js checks this
// flag after each poll and retries the load if a previous attempt failed.
export let loadConfigNeedsRetry = false;

function renderLoadConfig(settings) {
  loadConfigRowsEls.refresh.innerHTML = '';
  loadConfigRowsEls.application.innerHTML = '';
  loadConfigRowsEls.database.innerHTML = '';

  const ordered = [...settings].sort((a, b) =>
    (LOAD_CONFIG_LABELS[a.settingName]?.order ?? 999) - (LOAD_CONFIG_LABELS[b.settingName]?.order ?? 999));

  for (const setting of ordered) {
    const info = LOAD_CONFIG_LABELS[setting.settingName] || { label: setting.settingName, unit: '', group: 'application' };

    const row = document.createElement('div');
    row.className = 'loadconfig-row' + (info.boolean ? ' loadconfig-row--boolean' : info.single ? ' loadconfig-row--single' : '');
    row.dataset.settingName = setting.settingName;

    const labelCell = document.createElement('div');
    labelCell.className = 'loadconfig-label';
    labelCell.textContent = info.label;
    if (info.unit) {
      const unitEl = document.createElement('span');
      unitEl.className = 'loadconfig-unit';
      unitEl.textContent = info.unit;
      labelCell.appendChild(unitEl);
    }

    row.appendChild(labelCell);

    if (info.boolean) {
      // Still saved as Min === Max (0 or 1) under the hood.
      const radioGroup = document.createElement('div');
      radioGroup.className = 'loadconfig-radio-group';

      for (const [radioValue, radioLabel] of [['1', 'Enabled'], ['0', 'Disabled']]) {
        const optionLabel = document.createElement('label');
        optionLabel.className = 'loadconfig-radio-option';

        const radioInput = document.createElement('input');
        radioInput.type = 'radio';
        radioInput.name = 'radio-' + setting.settingName;
        radioInput.value = radioValue;
        radioInput.checked = (setting.min > 0) === (radioValue === '1');

        optionLabel.appendChild(radioInput);
        optionLabel.appendChild(document.createTextNode(radioLabel));
        radioGroup.appendChild(optionLabel);
      }

      row.appendChild(radioGroup);
    } else if (info.single) {
      // Still saved as Min === Max under the hood.
      const valueInput = document.createElement('input');
      valueInput.type = 'number';
      valueInput.min = '0';
      valueInput.className = 'loadconfig-input';
      valueInput.dataset.field = 'value';
      valueInput.value = setting.min;
      row.appendChild(valueInput);
    } else {
      const minInput = document.createElement('input');
      minInput.type = 'number';
      minInput.min = '0';
      minInput.className = 'loadconfig-input';
      minInput.dataset.field = 'min';
      minInput.value = setting.min;

      const maxInput = document.createElement('input');
      maxInput.type = 'number';
      maxInput.min = '0';
      maxInput.className = 'loadconfig-input';
      maxInput.dataset.field = 'max';
      maxInput.value = setting.max;

      row.appendChild(minInput);
      row.appendChild(maxInput);
    }

    loadConfigRowsEls[info.group].appendChild(row);
  }
}

let loadConfigCallId = 0;

export async function loadLoadConfig() {
  const callId = ++loadConfigCallId;
  try {
    const response = await fetchWithTimeout('/api/loadconfig', { cache: 'no-store' }, 5000);
    if (!response.ok) {
      throw new DbError(await dbErrorMessageFrom(response));
    }
    const settings = await response.json();
    if (callId !== loadConfigCallId) {
      return;
    }
    renderLoadConfig(settings);
    clearDbUnavailableIfShown();
    loadConfigNeedsRetry = false;
  } catch (err) {
    if (callId !== loadConfigCallId) {
      return;
    }
    setDbUnavailable(err instanceof DbError ? err.message : 'Database unavailable.');
    loadConfigNeedsRetry = true;
  }
}

loadConfigSaveBtn.addEventListener('click', async () => {
  const rows = [
    ...loadConfigRowsEls.refresh.querySelectorAll('.loadconfig-row'),
    ...loadConfigRowsEls.application.querySelectorAll('.loadconfig-row'),
    ...loadConfigRowsEls.database.querySelectorAll('.loadconfig-row')
  ];
  const settings = rows.map(row => {
    const checkedRadio = row.querySelector('input[type="radio"]:checked');
    if (checkedRadio) {
      const value = Number(checkedRadio.value);
      return { settingName: row.dataset.settingName, min: value, max: value };
    }
    const valueInput = row.querySelector('[data-field="value"]');
    if (valueInput) {
      const value = Number(valueInput.value);
      return { settingName: row.dataset.settingName, min: value, max: value };
    }
    return {
      settingName: row.dataset.settingName,
      min: Number(row.querySelector('[data-field="min"]').value),
      max: Number(row.querySelector('[data-field="max"]').value)
    };
  });

  const invalid = settings.find(s => !Number.isInteger(s.min) || !Number.isInteger(s.max) || s.min < 0 || s.min > s.max);
  if (invalid) {
    loadConfigStatusEl.textContent = 'Fix "' + invalid.settingName + '": Min must be >= 0 and <= Max.';
    return;
  }

  try {
    loadConfigStatusEl.textContent = 'Saving…';
    const saveResponse = await fetchWithAuth('/api/loadconfig', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settings)
    });
    if (!saveResponse.ok) {
      throw new Error('save failed (HTTP ' + saveResponse.status + ')');
    }

    loadConfigStatusEl.textContent = 'Saved. Takes effect within one poll interval.';
  } catch (err) {
    loadConfigStatusEl.textContent = 'Save failed (' + err.message + ').';
  }
});

loadLoadConfig();
