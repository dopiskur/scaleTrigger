// Shared indicator: shown regardless of whether the report poll or the LoadConfig fetch noticed the outage.
const dbUnavailableEl = document.getElementById('db-unavailable-indicator');

export function setDbUnavailable(message) {
  dbUnavailableEl.textContent = message;
  dbUnavailableEl.classList.add('status--error');
}

export function clearDbUnavailableIfShown() {
  if (dbUnavailableEl.classList.contains('status--error')) {
    dbUnavailableEl.textContent = '';
    dbUnavailableEl.classList.remove('status--error');
  }
}

// Thrown for a non-OK HTTP response carrying a server-provided message (e.g. "schema
// missing" vs "unavailable"), as opposed to a network/timeout failure with no such body.
export class DbError extends Error {}

export async function dbErrorMessageFrom(response) {
  try {
    const body = await response.json();
    if (body && typeof body.message === 'string' && body.message) {
      return body.message;
    }
  } catch {
    // Not a JSON body - fall through to the generic message.
  }
  return 'Database unavailable.';
}

// A hung earlier call could otherwise resolve after a later, faster one and
// overwrite fresh state with stale. AbortController caps how long a call can hang;
// the caller's own call-id guard ensures only the most recently started call touches the DOM.
export async function fetchWithTimeout(url, options, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...options, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}
