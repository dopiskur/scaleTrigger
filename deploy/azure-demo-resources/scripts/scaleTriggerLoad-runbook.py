"""
scaleTriggerLoad-runbook.py

Azure Automation (Python3 runbook) version of scaleTriggerLoad.py - generates the same
yes/no vote load against the ScaleTrigger API, but as a runbook job instead of a local
python.exe process. See scripts/scaleTriggerLoad.py for the local-CLI/aiohttp version;
Run-ScalingScenarios.ps1 still uses that one directly via Start-Process.

Standard library only (urllib.request + concurrent.futures.ThreadPoolExecutor, no
aiohttp) - Azure Automation's Python package management requires every dependency
(including aiohttp's compiled sub-dependencies) to be uploaded as an individual wheel by
hand, which doesn't fit a repeatable Bicep deployment.

Python runbooks take NO named parameters - Azure Automation passes them purely
positionally via sys.argv, in whatever order the caller's -Parameters ordered
dictionary lists them (Start-AzAutomationRunbook). Position below is that order -
sys.argv[1] is api_url, sys.argv[2] is votes_per_second, etc. Optional ones may be
passed as "" to take their default. Don't reorder these without updating every caller:

  1. api_url                  (required)
  2. votes_per_second         (default: 100 - matches Run-ScalingScenarios.ps1's --votes and BenchmarkTargetVotes)
  3. duration_seconds         (default: 60)
  4. ramp                     (default: false)  "true"/"false"
  5. ramp_step_percent        (default: 10)
  6. ramp_interval_seconds    (default: 5)
  7. ramp_max                 (default: none - unbounded growth)
  8. timeout_seconds          (default: 10)
  9. concurrency               (default: 200)
  10. report_interval_seconds (default: 5)
  11. username                (default: demoadmin - matches main.bicep's adminUsername default;
                                override if the target was deployed with a different -AdminUsername)
  12. password                (REQUIRED if the target API requires authentication - the deploy-time
                                -AdminPassword; no default, since unlike username it's a secret with
                                no safe guess. See Run-ScalingScenarios.ps1's own -AdminPassword.)
"""

import json
import random
import ssl
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from threading import Lock, Thread


def parse_args():
    argv = sys.argv[1:]

    def get(i, default=None):
        return argv[i] if i < len(argv) and argv[i] != "" else default

    api_url = get(0)
    if not api_url:
        raise SystemExit(
            "Usage (positional): api_url [votes_per_second] [duration_seconds] [ramp] "
            "[ramp_step_percent] [ramp_interval_seconds] [ramp_max] [timeout_seconds] "
            "[concurrency] [report_interval_seconds] [username] [password]"
        )

    return {
        "api_url": api_url.rstrip("/"),
        "votes_per_second": float(get(1, "100")),
        "duration_seconds": float(get(2, "60")),
        "ramp": get(3, "false").strip().lower() in ("true", "1", "yes"),
        "ramp_step_percent": float(get(4, "10")),
        "ramp_interval_seconds": float(get(5, "5")),
        "ramp_max": float(get(6)) if get(6) else None,
        "timeout_seconds": float(get(7, "10")),
        "concurrency": int(get(8, "200")),
        "report_interval_seconds": float(get(9, "5")),
        "username": get(10, "demoadmin"),
        "password": get(11),
    }


def make_ssl_context():
    """VM/VMSS scenarios serve HTTPS with a self-signed cert (Nginx) - verifying it would fail every request."""
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    return ctx


def http_request(method, url, ssl_context, timeout, headers=None, json_body=None):
    data = json.dumps(json_body).encode("utf-8") if json_body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers=dict(headers or {}))
    if json_body is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=timeout, context=ssl_context) as resp:
        return resp.status, resp.read()


def probe_requires_auth(api_url, ssl_context, timeout):
    """GET /api/auth/status; any failure is treated as "no auth" and left for the real load to surface."""
    try:
        _, body = http_request("GET", f"{api_url}/api/auth/status", ssl_context, timeout)
        return bool(json.loads(body).get("authRequired", False))
    except Exception:
        return False


def fetch_jwt_token(api_url, username, password, ssl_context, timeout):
    _, body = http_request(
        "POST", f"{api_url}/api/auth/login", ssl_context, timeout,
        json_body={"Username": username, "Password": password},
    )
    return json.loads(body)["token"]


class Stats:
    def __init__(self):
        self._lock = Lock()
        self.sent = 0
        self.ok = 0
        self.failed = 0
        self._latency_sum_ms = 0.0
        self._latency_max_ms = 0.0

    def record(self, success, latency_ms):
        with self._lock:
            self.sent += 1
            if success:
                self.ok += 1
                self._latency_sum_ms += latency_ms
                self._latency_max_ms = max(self._latency_max_ms, latency_ms)
            else:
                self.failed += 1

    def snapshot_and_reset(self):
        with self._lock:
            sent, ok, failed = self.sent, self.ok, self.failed
            avg_ms = self._latency_sum_ms / ok if ok else 0.0
            max_ms = self._latency_max_ms
            self.sent = self.ok = self.failed = 0
            self._latency_sum_ms = 0.0
            self._latency_max_ms = 0.0
        return {"sent": sent, "ok": ok, "failed": failed, "avg_ms": avg_ms, "max_ms": max_ms}


def build_ramp_schedule(starting_rate, ramp_step_percent, ramp_interval_seconds, duration_seconds, ramp_max):
    """(segment_start_seconds, votes_per_second) pairs; rate grows by ramp_step_percent every ramp_interval_seconds."""
    schedule = []
    t = 0.0
    rate = starting_rate
    while t < duration_seconds:
        schedule.append((t, rate))
        t += ramp_interval_seconds
        rate *= (1 + ramp_step_percent / 100)
        if ramp_max is not None:
            rate = min(rate, ramp_max)
    return schedule


def send_vote(api_url, option, headers, ssl_context, timeout, stats):
    start = time.perf_counter()
    try:
        status, _ = http_request(
            "POST", f"{api_url}/api/vote/add?option={option}", ssl_context, timeout, headers=headers,
        )
        success = status == 200
    except Exception:
        success = False
    stats.record(success, (time.perf_counter() - start) * 1000)


def report_loop(stats, interval_seconds, stop_flag):
    t0 = time.perf_counter()
    while not stop_flag["stop"]:
        time.sleep(interval_seconds)
        if stop_flag["stop"]:
            break
        elapsed = time.perf_counter() - t0
        s = stats.snapshot_and_reset()
        actual_votes_per_second = s["sent"] / interval_seconds
        print(
            f"t={elapsed:6.1f}s  sent={s['sent']:5d}  ok={s['ok']:5d}  failed={s['failed']:4d}  "
            f"actual_votes_per_second={actual_votes_per_second:6.1f}  "
            f"avg_latency_ms={s['avg_ms']:6.1f}  max_latency_ms={s['max_ms']:6.1f}"
        )


def generate_votes(api_url, votes_per_second, duration_seconds, headers, stats, concurrency,
                    ramp, ramp_step_percent, ramp_interval_seconds, ramp_max, timeout, ssl_context):
    schedule = (
        build_ramp_schedule(votes_per_second, ramp_step_percent, ramp_interval_seconds, duration_seconds, ramp_max)
        if ramp else [(0.0, votes_per_second)]
    )
    schedule_index = 0
    current_rate = schedule[0][1]

    start_time = time.perf_counter()
    end_time = start_time + duration_seconds
    next_tick = start_time

    in_flight = 0
    in_flight_lock = Lock()

    def on_done(_future):
        nonlocal in_flight
        with in_flight_lock:
            in_flight -= 1

    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        while time.perf_counter() < end_time:
            elapsed = time.perf_counter() - start_time

            while schedule_index + 1 < len(schedule) and elapsed >= schedule[schedule_index + 1][0]:
                schedule_index += 1
                current_rate = schedule[schedule_index][1]
                print(f"Ramp step: rate now {current_rate:.1f} votes/s")

            with in_flight_lock:
                at_capacity = in_flight >= concurrency

            # Mirrors the original's semaphore backpressure: don't let the target rate
            # race ahead of actual throughput by queuing unbounded work.
            if at_capacity:
                time.sleep(0.01)
                continue

            with in_flight_lock:
                in_flight += 1
            option = "yes" if random.random() < 0.5 else "no"
            pool.submit(send_vote, api_url, option, headers, ssl_context, timeout, stats).add_done_callback(on_done)

            interval = 1.0 / current_rate if current_rate > 0 else 0.1
            next_tick += interval
            sleep_for = next_tick - time.perf_counter()
            if sleep_for > 0:
                time.sleep(sleep_for)
            else:
                next_tick = time.perf_counter()

        while True:
            with in_flight_lock:
                remaining = in_flight
            if remaining == 0:
                break
            time.sleep(0.1)


def main():
    args = parse_args()
    ssl_context = make_ssl_context()

    print(f"Probing whether {args['api_url']} requires authentication...")
    requires_auth = probe_requires_auth(args["api_url"], ssl_context, args["timeout_seconds"])

    jwt_token = ""
    if requires_auth:
        if not args["password"]:
            raise SystemExit(
                f"{args['api_url']} requires authentication but no password was given (arg 12). "
                "Pass the same AdminUser:Password / -AdminPassword the target was deployed with - "
                "there is no safe default for it."
            )
        jwt_token = fetch_jwt_token(
            args["api_url"], args["username"], args["password"], ssl_context, args["timeout_seconds"],
        )
        print(f"API returned 401 - JWT token acquired (logged in as '{args['username']}').")
    else:
        print("API did not require authentication - proceeding without a token.")

    headers = {"Authorization": f"Bearer {jwt_token}"} if jwt_token else {}
    stats = Stats()

    stop_flag = {"stop": False}
    reporter = Thread(target=report_loop, args=(stats, args["report_interval_seconds"], stop_flag), daemon=True)
    reporter.start()

    try:
        generate_votes(
            args["api_url"], args["votes_per_second"], args["duration_seconds"], headers, stats,
            args["concurrency"], args["ramp"], args["ramp_step_percent"], args["ramp_interval_seconds"],
            args["ramp_max"], args["timeout_seconds"], ssl_context,
        )
    finally:
        stop_flag["stop"] = True

    print("Load generation finished.")


if __name__ == "__main__":
    main()
