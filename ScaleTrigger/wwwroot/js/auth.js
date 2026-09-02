// Shared across all admin actions so one login covers the rest of the session
// instead of re-prompting on every button.
let authToken = null;

function withAuthHeader(options) {
  return authToken
    ? { ...options, headers: { ...options.headers, Authorization: 'Bearer ' + authToken } }
    : options;
}

const loginOverlay = document.getElementById('login-overlay');
const loginForm = document.getElementById('login-form');
const loginUsernameEl = document.getElementById('login-username');
const loginPasswordEl = document.getElementById('login-password');
const loginStatusEl = document.getElementById('login-status');
const loginCancelBtn = document.getElementById('login-cancel-btn');

// Shows the login modal and resolves with { username, password }, or null if
// cancelled. errorMessage (if given) is shown so a failed attempt can be retried
// without losing context on why.
function promptLogin(errorMessage) {
  return new Promise((resolve) => {
    loginStatusEl.textContent = errorMessage || '';
    loginStatusEl.classList.toggle('status--error', !!errorMessage);
    loginUsernameEl.value = '';
    loginPasswordEl.value = '';
    loginOverlay.hidden = false;
    loginUsernameEl.focus();

    function cleanup() {
      loginOverlay.hidden = true;
      loginForm.removeEventListener('submit', onSubmit);
      loginCancelBtn.removeEventListener('click', onCancel);
    }

    function onSubmit(event) {
      event.preventDefault();
      const username = loginUsernameEl.value;
      const password = loginPasswordEl.value;
      cleanup();
      resolve({ username, password });
    }

    function onCancel() {
      cleanup();
      resolve(null);
    }

    loginForm.addEventListener('submit', onSubmit);
    loginCancelBtn.addEventListener('click', onCancel);
  });
}

// Retries once with a freshly prompted login if the server responds 401; loops
// the login modal on a failed attempt instead of failing the whole action.
export async function fetchWithAuth(url, options = {}) {
  const response = await fetch(url, withAuthHeader(options));
  if (response.status !== 401) {
    return response;
  }

  let error;
  while (true) {
    const credentials = await promptLogin(error);
    if (!credentials) {
      throw new Error('login cancelled');
    }

    const loginResponse = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(credentials)
    });
    if (loginResponse.ok) {
      ({ token: authToken } = await loginResponse.json());
      return fetch(url, withAuthHeader(options));
    }

    error = 'Login failed (HTTP ' + loginResponse.status + ').';
  }
}
