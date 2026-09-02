#!/usr/bin/env python3
"""
scaleTriggerLoad.py

External load generator for the ScaleTrigger API.

Sends POST /api/vote/add?option=yes|no to a real, external URL, randomly
choosing yes/no per request. Supports several parallel processes so a
single process's GIL/network overhead doesn't cap the achievable rate.

Authentication is auto-detected: GET /api/auth/status is checked first, and
if it reports authRequired=true, the script logs in and attaches the
resulting JWT to every subsequent request. Defaults to admin:admin; pass
--username/--password if the target was deployed with different credentials
(e.g. any azure-demo-resources scenario).

Ramp-up mode (--ramp true): --votes becomes the starting rate, increasing
by --ramp-step percent every --ramp-interval seconds for the rest of the
run - useful for finding an instance's breaking point before autoscale kicks in.

Examples:

    # 20 votes/s - authentication is detected automatically
    python scripts/scaleTriggerLoad.py --url https://scaletrigger-api.azurewebsites.net \
        --votes 20 --duration 60

    # 200 votes/s, spread across 4 parallel processes
    python scripts/scaleTriggerLoad.py --url https://scaletrigger-api.azurewebsites.net \
        --votes 200 --duration 120 --process 4

    # Ramp-up: start at 10 votes/s, +10% every 5s, for 5 minutes
    python scripts/scaleTriggerLoad.py --url https://scaletrigger-api.azurewebsites.net \
        --votes 10 --duration 300 \
        --ramp true --ramp-step 10 --ramp-interval 5

Requires: aiohttp (the script will offer to install it automatically
if missing).

Parameters:

    --url                                   (required)
        Base URL of the API, e.g. https://xyz.azurewebsites.net

    --votes (per second)                    (required)
        Total target votes per second (yes/no random, 50/50), spread across all processes.

    --duration (seconds)                     (optional, default: 60)
        Test duration in seconds.

    --process (count)                        (optional, default: 1)
        Number of parallel processes jointly generating traffic.

    --concurrency (per process)               (optional, default: 200)
        Max concurrent in-flight requests per process; caps memory/socket growth
        if the API responds slower than the target rate.

    --report (interval in seconds)           (optional, default: 5)
        Interval for printing statistics, in seconds.

    --ramp (connection increase)             (optional, default: false)
        true/false. If true, --votes is the starting rate, increasing by
        --ramp-step percent every --ramp-interval seconds for the rest of the run.

    --ramp-step (percent)                    (optional, default: 10)
        Percentage the rate increases by at each ramp step. Only used when --ramp is true.

    --ramp-interval (seconds)                (optional, default: 5)
        How often the rate increases when --ramp is true.

    --ramp-max (votes per second)            (optional, default: none)
        Caps the rate so it holds steady once reached, instead of growing unbounded.
        Only used when --ramp is true.

    --timeout (seconds per request)          (optional, default: 10)
        Max time to wait for a single vote request before treating it as failed
        (aiohttp's default is 300s, which would hold a concurrency slot too long).

    --username                               (optional, default: admin)
        Login used if the API requires authentication (GET /api/auth/status).

    --password                               (optional, default: admin)
        Password used if the API requires authentication (GET /api/auth/status).
"""

import subprocess
import sys

try:
    import aiohttp
except ImportError:
    answer = input(
        "The 'aiohttp' package is not installed, but it is required to run this script.\n"
        "Install it now with pip? (y/n): "
    ).strip().lower()
    if answer == "y":
        subprocess.check_call([sys.executable, "-m", "pip", "install", "aiohttp"])
        import aiohttp  # noqa: F401 (re-import after install)
    else:
        raise SystemExit(
            "Aborted - 'aiohttp' is required. Install it manually with:\n\n"
            "    pip install aiohttp\n"
        )

import argparse
import asyncio
import multiprocessing
import random
import time
from dataclasses import dataclass, field

# Defaults for --username/--password, matching the API's default AdminUser:Username/Password.
JWT_USERNAME = "admin"
JWT_PASSWORD = "admin"


@dataclass
class Stats:
    sent: int = 0
    ok: int = 0
    failed: int = 0
    latencies_ms: list = field(default_factory=list)

    def record(self, success: bool, latency_ms: float):
        self.sent += 1
        if success:
            self.ok += 1
            self.latencies_ms.append(latency_ms)
        else:
            self.failed += 1

    def snapshot_and_reset(self):
        lat = self.latencies_ms
        summary = {
            "sent": self.sent,
            "ok": self.ok,
            "failed": self.failed,
            "avg_ms": sum(lat) / len(lat) if lat else 0.0,
            "max_ms": max(lat) if lat else 0.0,
        }
        self.sent = self.ok = self.failed = 0
        self.latencies_ms = []
        return summary


class SharedStats:
    """Same interface as Stats, but backed by multiprocessing shared memory + a lock, so every process accumulates into one combined total."""

    def __init__(self):
        self._lock = multiprocessing.Lock()
        self._sent = multiprocessing.Value("l", 0)
        self._ok = multiprocessing.Value("l", 0)
        self._failed = multiprocessing.Value("l", 0)
        self._latency_sum_ms = multiprocessing.Value("d", 0.0)
        self._latency_max_ms = multiprocessing.Value("d", 0.0)

    def record(self, success: bool, latency_ms: float):
        with self._lock:
            self._sent.value += 1
            if success:
                self._ok.value += 1
                self._latency_sum_ms.value += latency_ms
                if latency_ms > self._latency_max_ms.value:
                    self._latency_max_ms.value = latency_ms
            else:
                self._failed.value += 1

    def snapshot_and_reset(self):
        with self._lock:
            sent, ok, failed = self._sent.value, self._ok.value, self._failed.value
            latency_sum_ms, latency_max_ms = self._latency_sum_ms.value, self._latency_max_ms.value
            self._sent.value = 0
            self._ok.value = 0
            self._failed.value = 0
            self._latency_sum_ms.value = 0.0
            self._latency_max_ms.value = 0.0
        return {
            "sent": sent,
            "ok": ok,
            "failed": failed,
            "avg_ms": latency_sum_ms / ok if ok else 0.0,
            "max_ms": latency_max_ms,
        }


def _new_client_session(timeout: aiohttp.ClientTimeout, **connector_kwargs) -> aiohttp.ClientSession:
    """Disables cert verification - the VM/VMSS scenarios serve HTTPS with a self-signed cert aiohttp otherwise rejects outright."""
    connector = aiohttp.TCPConnector(ssl=False, **connector_kwargs)
    return aiohttp.ClientSession(connector=connector, timeout=timeout)


async def fetch_jwt_token(api_url: str, username: str, password: str, timeout_seconds: float) -> str:
    """Fetches a JWT token via POST /api/auth/login."""
    timeout = aiohttp.ClientTimeout(total=timeout_seconds)
    async with _new_client_session(timeout) as session:
        async with session.post(
            f"{api_url}/api/auth/login",
            json={"Username": username, "Password": password},
        ) as resp:
            resp.raise_for_status()
            data = await resp.json()
            return data["token"]


async def probe_requires_auth(api_url: str, timeout_seconds: float) -> bool:
    """Detects Auth:Enabled via GET /api/auth/status - side-effect-free, unlike the old probe vote. If the check fails outright, assumes no auth and lets the real load test surface the problem."""
    timeout = aiohttp.ClientTimeout(total=timeout_seconds)
    async with _new_client_session(timeout) as session:
        try:
            async with session.get(f"{api_url}/api/auth/status") as resp:
                data = await resp.json()
                return bool(data.get("authRequired", False))
        except Exception:
            return False


async def send_vote_request(session: aiohttp.ClientSession, api_url: str, option: str,
                             headers: dict, stats):
    start = time.perf_counter()
    try:
        async with session.post(
            f"{api_url}/api/vote/add",
            params={"option": option},
            headers=headers,
        ) as resp:
            success = resp.status == 200
            await resp.read()
    except Exception:
        success = False
    latency_ms = (time.perf_counter() - start) * 1000
    stats.record(success, latency_ms)


def pick_random_option() -> str:
    return "yes" if random.random() < 0.5 else "no"


def build_ramp_schedule(starting_rate: float, ramp_step_percent: float,
                         ramp_interval_seconds: float, duration_seconds: float,
                         ramp_max: float = None):
    """Builds (segment_start_seconds, votes_per_second) pairs; rate grows by ramp_step_percent every ramp_interval_seconds, capped at ramp_max if set."""
    schedule = []
    t = 0.0
    rate = starting_rate
    while t < duration_seconds:
        schedule.append((t, rate))
        t += ramp_interval_seconds
        rate = rate * (1 + ramp_step_percent / 100)
        if ramp_max is not None:
            rate = min(rate, ramp_max)
    return schedule


async def generate_votes(api_url: str, votes_per_second: float, duration_seconds: float,
                          headers: dict, stats, max_in_flight_requests: int,
                          ramp_enabled: bool, ramp_step_percent: float, ramp_interval_seconds: float,
                          ramp_max: float, process_label: str, total_process_count: int,
                          timeout_seconds: float):
    """
    Fires one request at a time, bounded by a semaphore capping in-flight
    requests. If ramp_enabled, votes_per_second is the starting rate and
    grows per a precomputed schedule; only process-1 prints ramp-step lines
    since every process shares the same schedule shape.
    """
    semaphore = asyncio.Semaphore(max_in_flight_requests)
    in_flight = 0
    start_time = time.perf_counter()
    end_time = start_time + duration_seconds

    schedule = (
        build_ramp_schedule(votes_per_second, ramp_step_percent, ramp_interval_seconds,
                             duration_seconds, ramp_max)
        if ramp_enabled else [(0.0, votes_per_second)]
    )
    schedule_index = 0
    current_rate = schedule[0][1]

    timeout = aiohttp.ClientTimeout(total=timeout_seconds)
    async with _new_client_session(timeout, limit=max_in_flight_requests + 10) as session:

        async def release_after_send(option: str):
            nonlocal in_flight
            in_flight += 1
            try:
                await send_vote_request(session, api_url, option, headers, stats)
            finally:
                in_flight -= 1
                semaphore.release()

        next_tick = time.perf_counter()
        while time.perf_counter() < end_time:
            elapsed = time.perf_counter() - start_time

            # Advance to the next ramp step once its start time is reached.
            while schedule_index + 1 < len(schedule) and elapsed >= schedule[schedule_index + 1][0]:
                schedule_index += 1
                current_rate = schedule[schedule_index][1]
                if process_label == "process-1":
                    total_rate = current_rate * total_process_count
                    print(
                        f"Ramp step: rate now {current_rate:.1f} votes/s average per process "
                        f"({total_rate:.1f} total across {total_process_count} process"
                        f"{'es' if total_process_count != 1 else ''})"
                    )

            interval_seconds = 1.0 / current_rate if current_rate > 0 else 0.1
            option = pick_random_option()

            # Blocks here once max_in_flight_requests is reached, instead of piling up
            # tasks the semaphore hasn't let run yet - caps memory at concurrency, not
            # at however far the ramped target has raced ahead of actual throughput.
            await semaphore.acquire()
            asyncio.create_task(release_after_send(option))

            next_tick += interval_seconds
            sleep_for = next_tick - time.perf_counter()
            if sleep_for > 0:
                await asyncio.sleep(sleep_for)
            else:
                # Always yield - without this, once the target rate outruns real
                # throughput the loop never awaits, starving the event loop so no
                # task (including the semaphore-gated ones) ever gets to run.
                await asyncio.sleep(0)
                next_tick = time.perf_counter()

        # wait for remaining in-flight requests to drain
        await asyncio.sleep(0.5)
        while in_flight > 0:
            await asyncio.sleep(0.1)


async def print_periodic_report(stats, report_interval_seconds: float, stop_event: asyncio.Event):
    t0 = time.perf_counter()
    while not stop_event.is_set():
        await asyncio.sleep(report_interval_seconds)
        elapsed = time.perf_counter() - t0
        s = stats.snapshot_and_reset()
        actual_votes_per_second = s["sent"] / report_interval_seconds
        print(
            f"t={elapsed:6.1f}s  sent={s['sent']:5d}  ok={s['ok']:5d}  "
            f"failed={s['failed']:4d}  actual_votes_per_second={actual_votes_per_second:6.1f}  "
            f"avg_latency_ms={s['avg_ms']:6.1f}  max_latency_ms={s['max_ms']:6.1f}"
        )


async def run_single_process_worker(api_url: str, votes_per_second: float, duration_seconds: float,
                                     jwt_token: str, max_in_flight_requests: int,
                                     report_interval_seconds: float,
                                     ramp_enabled: bool, ramp_step_percent: float, ramp_interval_seconds: float,
                                     ramp_max: float, process_label: str, total_process_count: int,
                                     shared_stats: SharedStats, timeout_seconds: float):
    headers = {"Authorization": f"Bearer {jwt_token}"} if jwt_token else {}

    stop_event = asyncio.Event()

    # Only process-1 prints the report; shared_stats already aggregates every process.
    report_task = None
    if process_label == "process-1":
        report_task = asyncio.create_task(
            print_periodic_report(shared_stats, report_interval_seconds, stop_event)
        )

    await generate_votes(
        api_url=api_url,
        votes_per_second=votes_per_second,
        duration_seconds=duration_seconds,
        headers=headers,
        stats=shared_stats,
        max_in_flight_requests=max_in_flight_requests,
        ramp_enabled=ramp_enabled,
        ramp_step_percent=ramp_step_percent,
        ramp_interval_seconds=ramp_interval_seconds,
        ramp_max=ramp_max,
        process_label=process_label,
        total_process_count=total_process_count,
        timeout_seconds=timeout_seconds,
    )

    stop_event.set()
    if report_task:
        report_task.cancel()


def process_entry_point(api_url: str, votes_per_second: float, duration_seconds: float,
                         jwt_token: str, max_in_flight_requests: int,
                         report_interval_seconds: float,
                         ramp_enabled: bool, ramp_step_percent: float, ramp_interval_seconds: float,
                         ramp_max: float, process_label: str, total_process_count: int,
                         shared_stats: SharedStats, timeout_seconds: float):
    asyncio.run(run_single_process_worker(
        api_url, votes_per_second, duration_seconds, jwt_token,
        max_in_flight_requests, report_interval_seconds,
        ramp_enabled, ramp_step_percent, ramp_interval_seconds, ramp_max, process_label, total_process_count,
        shared_stats, timeout_seconds,
    ))


PARAMETER_REFERENCE = [
    # (flag, example_value, required, default_value_or_None)
    ("--url", "https://xyz.azurewebsites.net", "yes", None),
    ("--votes (per second)", "50", "yes", None),
    ("--duration (seconds)", "60", "no", "60"),
    ("--process (count)", "1", "no", "1"),
    ("--concurrency (per process)", "200", "no", "200"),
    ("--report (interval in seconds)", "5", "no", "5"),
    ("--ramp (connection increase)", "false", "no", "false"),
    ("--ramp-step (percent)", "10", "no", "10"),
    ("--ramp-interval (seconds)", "5", "no", "5"),
    ("--ramp-max (votes per second)", "500", "no", None),
    ("--timeout (seconds per request)", "10", "no", "10"),
]


def print_parameter_help():
    print("No arguments provided. Available parameters:\n")
    flag_width = max(len(flag) for flag, _, _, _ in PARAMETER_REFERENCE) + 2
    example_width = max(len(example) for _, example, _, default in PARAMETER_REFERENCE if default is not None) + 1
    for flag, example, required, default in PARAMETER_REFERENCE:
        if default is not None:
            print(f"  {flag:<{flag_width}} required: {required:<5} example: {example:<{example_width}} (default)")
        else:
            print(f"  {flag:<{flag_width}} required: {required:<5} example: {example}")
    print("\nRun with -h or --help for full descriptions and usage examples.")


def str2bool(value: str) -> bool:
    if value.lower() in ("true", "1", "yes"):
        return True
    if value.lower() in ("false", "0", "no"):
        return False
    raise argparse.ArgumentTypeError(f"Expected true/false, got: {value}")


def parse_args():
    p = argparse.ArgumentParser(
        description="Load generator for the ScaleTrigger API",
        epilog="""
Examples:

  20 votes/s - authentication is detected automatically:
    python scripts/scaleTriggerLoad.py --url https://scaletrigger-api.azurewebsites.net \\
        --votes 20 --duration 60

  200 votes/s, spread across 4 parallel processes:
    python scripts/scaleTriggerLoad.py --url https://scaletrigger-api.azurewebsites.net \\
        --votes 200 --duration 120 --process 4

  Ramp-up: start at 10 votes/s, +10% every 5s, capped at 500 votes/s, for 5 minutes:
    python scripts/scaleTriggerLoad.py --url https://scaletrigger-api.azurewebsites.net \\
        --votes 10 --duration 300 \\
        --ramp true --ramp-step 10 --ramp-interval 5 --ramp-max 500

Note: requires 'aiohttp' (the script offers to install it automatically if missing).
""",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("--url", dest="api_url", metavar="URL", required=True,
                   help="Base URL of the API, e.g. https://xyz.azurewebsites.net")
    p.add_argument("--votes", dest="votes_per_second", metavar="COUNT", type=float, required=True,
                   help="Total target number of votes (per second) (yes/no chosen randomly, 50/50), "
                        "spread evenly across all processes")
    p.add_argument("--duration", dest="duration_seconds", metavar="SECONDS", type=float, default=60,
                   help="Test duration in seconds (default: 60)")
    p.add_argument("--process", dest="process_count", metavar="COUNT", type=int, default=1,
                   help="Number of parallel processes (count) jointly generating traffic (default: 1)")
    p.add_argument("--concurrency", dest="max_in_flight_requests_per_process", metavar="COUNT",
                   type=int, default=200,
                   help="Max number of concurrent in-flight requests per process (default: 200)")
    p.add_argument("--report", dest="report_interval_seconds", metavar="SECONDS", type=float, default=5,
                   help="Interval for printing statistics, in seconds (default: 5)")
    p.add_argument("--ramp", type=str2bool, default=False, metavar="true|false",
                   help="(connection increase) If true, --votes is the starting rate and it "
                        "increases by --ramp-step percent every --ramp-interval seconds for the "
                        "rest of the run (default: false)")
    p.add_argument("--ramp-step", dest="ramp_step_percent", metavar="PERCENT", type=float, default=10,
                   help="Percentage the rate increases by at each ramp step; only used when "
                        "--ramp is true (default: 10)")
    p.add_argument("--ramp-interval", dest="ramp_interval_seconds", metavar="SECONDS", type=float, default=5,
                   help="How often the rate increases when --ramp is true (default: 5)")
    p.add_argument("--ramp-max", metavar="VOTES", type=float, default=None,
                   help="Caps the rate so it stops increasing once this value is reached, "
                        "holding steady for the rest of the run; only used when --ramp is true "
                        "(default: no cap, unlimited exponential growth)")
    p.add_argument("--timeout", dest="timeout_seconds", metavar="SECONDS", type=float, default=10,
                   help="Max time to wait for a single request (connect + response) before "
                        "treating it as failed, so a server that never responds doesn't hold "
                        "a concurrency slot for aiohttp's 300s default (default: 10)")
    p.add_argument("--username", default=JWT_USERNAME,
                   help=f"Login used if the API requires authentication (default: {JWT_USERNAME})")
    p.add_argument("--password", default=JWT_PASSWORD,
                   help="Password used if the API requires authentication (default: matches "
                        "--username's default)")
    return p.parse_args()


def main():
    args = parse_args()

    print("Probing whether the API requires authentication...")
    requires_auth = asyncio.run(probe_requires_auth(args.api_url, args.timeout_seconds))

    jwt_token = ""
    if requires_auth:
        jwt_token = asyncio.run(fetch_jwt_token(args.api_url, args.username, args.password, args.timeout_seconds))
        print(f"API returned 401 - JWT token acquired automatically (logged in as '{args.username}').")
    else:
        print("API did not require authentication - proceeding without a token.")

    votes_per_second_per_process = args.votes_per_second / args.process_count
    ramp_max_per_process = args.ramp_max / args.process_count if args.ramp_max is not None else None
    shared_stats = SharedStats()

    if args.process_count == 1:
        process_entry_point(
            args.api_url, votes_per_second_per_process, args.duration_seconds, jwt_token,
            args.max_in_flight_requests_per_process, args.report_interval_seconds,
            args.ramp, args.ramp_step_percent, args.ramp_interval_seconds, ramp_max_per_process,
            "process-1", args.process_count, shared_stats, args.timeout_seconds,
        )
        print("All processes finished.")
        return

    processes = []
    for i in range(args.process_count):
        proc = multiprocessing.Process(
            target=process_entry_point,
            args=(
                args.api_url, votes_per_second_per_process, args.duration_seconds, jwt_token,
                args.max_in_flight_requests_per_process, args.report_interval_seconds,
                args.ramp, args.ramp_step_percent, args.ramp_interval_seconds, ramp_max_per_process,
                f"process-{i + 1}", args.process_count, shared_stats, args.timeout_seconds,
            ),
        )
        processes.append(proc)
        proc.start()

    for proc in processes:
        proc.join()

    print("All processes finished.")


if __name__ == "__main__":
    if len(sys.argv) == 1:
        print_parameter_help()
        raise SystemExit(0)
    main()
