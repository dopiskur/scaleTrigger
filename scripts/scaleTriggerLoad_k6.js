/*
 * scaleTriggerLoad_k6.js
 *
 * k6 load-test script for the ScaleTrigger API.
 *
 * Sends POST /api/vote/add?option=yes|no, randomly choosing yes/no per
 * request. Uses k6's ramping-arrival-rate executor, which targets a fixed
 * number of requests per second regardless of how many VUs that takes.
 *
 * Authentication is auto-detected: setup() sends a single probe vote
 * before the test starts, and if the API responds 401, logs in with
 * admin:admin (override via AUTH_USER / AUTH_PASS) and every VU attaches
 * the resulting JWT as a Bearer token.
 *
 * Install k6: https://k6.io/docs/get-started/installation/
 *
 * Examples:
 *
 *   # 20 votes/s for 60s - authentication is detected automatically
 *   k6 run -e URL=https://scaletrigger-api.azurewebsites.net \
 *       -e VOTES=20 -e DURATION=60 scaleTriggerLoad_k6.js
 *
 *   # Ramp-up: start at 10 votes/s, +10% every 5s, capped at 500 votes/s, for 5 minutes
 *   k6 run -e URL=https://scaletrigger-api.azurewebsites.net \
 *       -e VOTES=10 -e DURATION=300 \
 *       -e RAMP=true -e RAMP_STEP=10 -e RAMP_INTERVAL=5 -e RAMP_MAX=500 \
 *       scaleTriggerLoad_k6.js
 *
 *   # Against a VM/VMSS demo endpoint's self-signed cert
 *   k6 run -e URL=https://vm-public-ip -e INSECURE_TLS=true \
 *       -e VOTES=20 -e DURATION=60 scaleTriggerLoad_k6.js
 *
 * Environment variables:
 *
 *   URL              (required) Base URL of the API, e.g. https://xyz.azurewebsites.net
 *   VOTES            (default: 20)    Target votes per second (starting rate if RAMP=true)
 *   DURATION         (default: 60)    Total test duration in seconds
 *   RAMP             (default: false) true/false - if true, VOTES is the starting rate and
 *                                     it increases by RAMP_STEP percent every RAMP_INTERVAL
 *                                     seconds for the rest of the run
 *   RAMP_STEP        (default: 10)    Percentage the rate increases by at each ramp step
 *   RAMP_INTERVAL    (default: 5)     How often the rate increases, in seconds
 *   RAMP_MAX         (default: none)  Caps the rate so it stops increasing once reached;
 *                                     leave unset for unlimited (exponential) growth
 *   MAX_VUS          (default: 500)   Upper bound on concurrent VUs k6 may spin up to sustain
 *                                     the target rate - raise it if k6 warns it can't keep up
 *   TIMEOUT          (default: 10s)   Max time to wait for a single request
 *   AUTH_USER        (default: admin) Username used to log in if the API requires auth
 *   AUTH_PASS        (default: admin) Password used to log in if the API requires auth
 *   INSECURE_TLS     (default: false) true/false - skip TLS certificate verification, for the
 *                                     self-signed cert the VM/VMSS demo scenarios serve over
 *                                     HTTPS (scaleTriggerLoad.py and Run-ScalingScenarios.ps1
 *                                     handle the same certificate unconditionally, since they
 *                                     target VM/VMSS specifically; this script also runs against
 *                                     App Service/Container Apps/AKS with a real certificate, so
 *                                     it's opt-in here instead - leave it false there).
 *
 * Run with -e RAMP=false (or omit it) for a flat, fixed-rate run instead of a ramp.
 */

import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.URL;
if (!BASE_URL) {
  throw new Error('Missing required -e URL=https://your-api-url');
}

const START_RATE = Number(__ENV.VOTES || 20);
const DURATION_SECONDS = Number(__ENV.DURATION || 60);
const RAMP = (__ENV.RAMP || 'false').toLowerCase() === 'true';
const RAMP_STEP_PERCENT = Number(__ENV.RAMP_STEP || 10);
const RAMP_INTERVAL_SECONDS = Number(__ENV.RAMP_INTERVAL || 5);
const RAMP_MAX = __ENV.RAMP_MAX ? Number(__ENV.RAMP_MAX) : null;
const MAX_VUS = Number(__ENV.MAX_VUS || 500);
const TIMEOUT = __ENV.TIMEOUT || '10s';
const AUTH_USER = __ENV.AUTH_USER || 'admin';
const AUTH_PASS = __ENV.AUTH_PASS || 'admin';
const INSECURE_TLS = (__ENV.INSECURE_TLS || 'false').toLowerCase() === 'true';

// Builds k6 "stages" (target rate + duration pairs): every RAMP_INTERVAL_SECONDS the
// rate grows by RAMP_STEP_PERCENT, capped at RAMP_MAX, until DURATION_SECONDS is covered.
// One flat stage at START_RATE if RAMP is false.
function buildStages() {
  if (!RAMP) {
    return [{ target: START_RATE, duration: `${DURATION_SECONDS}s` }];
  }

  const stages = [];
  let rate = START_RATE;
  let elapsedSeconds = 0;

  while (elapsedSeconds < DURATION_SECONDS) {
    const stepSeconds = Math.min(RAMP_INTERVAL_SECONDS, DURATION_SECONDS - elapsedSeconds);
    stages.push({ target: Math.round(rate), duration: `${stepSeconds}s` });
    rate = rate * (1 + RAMP_STEP_PERCENT / 100);
    if (RAMP_MAX) {
      rate = Math.min(rate, RAMP_MAX);
    }
    elapsedSeconds += stepSeconds;
  }

  return stages;
}

export const options = {
  insecureSkipTLSVerify: INSECURE_TLS,
  scenarios: {
    votes: {
      executor: 'ramping-arrival-rate',
      startRate: START_RATE,
      timeUnit: '1s',
      preAllocatedVUs: Math.min(MAX_VUS, 50),
      maxVUs: MAX_VUS,
      stages: buildStages(),
    },
  },
  thresholds: {
    // Fails the run (non-zero exit code) above a 5% error rate; adjust or remove for CI.
    http_req_failed: ['rate<0.05'],
  },
};

export function setup() {
  const probe = http.post(`${BASE_URL}/api/vote/add?option=yes`, null, { timeout: TIMEOUT });

  if (probe.status !== 401) {
    return { token: '' };
  }

  const loginRes = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ username: AUTH_USER, password: AUTH_PASS }),
    { headers: { 'Content-Type': 'application/json' }, timeout: TIMEOUT }
  );

  check(loginRes, { 'login succeeded': (r) => r.status === 200 });

  return { token: loginRes.json('token') || '' };
}

export default function (data) {
  const option = Math.random() < 0.5 ? 'yes' : 'no';
  const headers = data.token ? { Authorization: `Bearer ${data.token}` } : {};

  const res = http.post(`${BASE_URL}/api/vote/add?option=${option}`, null, {
    headers,
    timeout: TIMEOUT,
  });

  check(res, { 'status is 200': (r) => r.status === 200 });
}
