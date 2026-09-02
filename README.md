# ScaleTrigger

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A REST API for testing autoscale triggers on the app tier (Azure App Service, Container Apps, AKS, or anywhere else) and on the database tier (e.g. Azure SQL serverless, Flexible Server autoscale). How much CPU/memory/disk/network each request burns, in the app and separately inside the database itself, can be turned up or down **live**, from a dashboard or a single API call, while traffic keeps flowing. No redeploy, no restart, and no need to stop the run and repeat the whole experiment just to try a different intensity; scale-up and scale-down thresholds can both be swept in the same run.

The solution targets **.NET 10 (LTS, supported until November 2028)**. The .NET 10 SDK is required.

**Primary use cases:** demos and training sessions (MCT / Azure training), personal/internal experimentation, and proving/documenting autoscale behavior.

## How it works

Every `POST /api/vote/add?option=yes|no` call:

1. Records a vote in the database.
2. Burns a controllable, randomized amount of CPU, memory, disk I/O, and network latency (the actual point of the benchmark).

### Live-tunable load

Every `POST /api/vote/add` call picks a fresh random value, per load type, from a `Min`/`Max` range. Unlike a value baked into `appsettings.json`, these ranges live in the `LoadConfig` database table and can be changed while the app is running and under test, with no restart or redeploy:

- `CpuIterationsPerVote`: chained SHA-512 hashing in the app process (each hash's output feeds the next, so the JIT can't fold the loop away)
- `MemoryKilobytesPerVote`: allocated in 1 MB chunks with a small delay between them (a visible ramp, touched page-by-page so the OS actually reserves physical memory, not just address space) instead of one synchronous allocation. The real OOM-kill risk isn't this setting's `Max` in isolation - it's **concurrency × randomized size per request**: dozens of parallel votes can each draw a high value at once and the sum can overrun the instance regardless of how large that instance is. `LoadSafety:MaxConcurrentMemoryBytes` (`appsettings.json`, default 2 GB) caps the total bytes reserved by in-flight allocations across all requests on the instance; once a burst hits that ceiling, additional votes just skip their memory component (CPU/disk/network/DB load still runs) instead of piling on. Set it below the instance's actual RAM (e.g. 5-6 GB on an 8 GB instance) to leave headroom for the OS/GC/other requests
- `DiskWriteKilobytesPerVote`: a uniquely-named temp file per call, written with `FileOptions.WriteThrough` + `Flush(true)` (real disk I/O, not page cache) then deleted immediately
- `NetworkLatencyMillisecondsPerVote`: a non-blocking `Task.Delay`, so it frees the request thread the way a real downstream call would
- `PayloadBytesPerVote`: optional extra random bytes written into a `Payload` table row (off by default, since unlike the others this permanently grows the database; opt in for write-throughput benchmarking)
- `DbCpuIterationsPerVote`: CPU burn that runs inside the database engine itself (a `DbCpuBurn` stored procedure/function), not the app; add `&dbCpuBurnOnly=true` to `POST /api/vote/add` to isolate it from the `Vote`/`Payload` insert entirely
- `ConfigRefresh`: not a vote-time load setting; it's how often (in seconds) the app re-polls `LoadConfig` for changes made via the dashboard/API, so it controls how fast an edit to any of the above takes effect
- `LoadEnabled`: also not a per-vote load setting; `0`/`1` master switch for per-vote load *and* the `Vote`/`Payload` write. While `0`, `POST /api/vote/add` is a fast no-op - the dashboard's "Discard backlog" button flips this off, waits a few seconds, then flips it back on, so requests already queued behind a heavy load config drain near-instantly instead of each running its full configured cost
- `CacheEnabled`: also not a per-vote load setting; `0`/`1` toggle for whether `GET /api/vote/report` is cached in memory. Unlike `Cache:SlidingExpirationMinutes` (a one-time Application Setting), this lives in the database like everything else in this section, so every node picks up the same value on a scale-out instead of each starting from its own `appsettings.json`

Results can be read directly from the `Vote` table or via `GET /api/vote/report`, which returns the total vote count and payload stats, fully computed by the database (a stored procedure/function, or plain SQL on SQLite, see "Database providers").

## Why not just use Chaos Studio / AWS FIS / Chaos Mesh / stress-ng?

None of them offer a live dial you can turn while traffic keeps flowing and watch the very next request pick up the new value. That's specifically what ScaleTrigger's `LoadConfig` table + dashboard does. It also means the load is exercised as real HTTP traffic through your actual routing/ingress/load balancer path, not injected directly at the VM/pod level, which matters if what you're validating is a scaler that reacts to request rate or concurrency rather than raw CPU%, since Chaos Studio's agent-based faults don't exercise that on their own.

Those are legitimate, more mature tools for the general "put CPU/memory pressure on something" problem, but:
- Chaos Studio / AWS FIS: change the level → define or launch a new experiment (or a pre-scripted sequence of steps, decided in advance).
- Chaos Mesh: change the level → edit the CRD and `kubectl apply` it again.
- stress-ng: change the level → kill the process and restart it with new flags.
- k6 / Locust / Azure Load Testing: don't control per-request resource cost at all; they control requests per second, and what each request costs server-side is entirely up to the target app.

None of this makes ScaleTrigger a replacement for those tools. For infrastructure-level fault injection (killing instances, network faults, service failures) it does nothing at all, and for a one-off "just burn 80% CPU for five minutes" test, stress-ng is simpler and needs zero custom code. Use ScaleTrigger specifically when you want to sweep or fine-tune a request-driven load profile live, without interrupting the traffic that's already hitting the trigger you're testing.

## Quickstart

**Azure**: one click, provisions and deploys everything.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fdopiskur%2FscaleTrigger%2Fmaster%2Fdeploy%2Fazure%2Fmain.json)

For repeatable deploys afterward or a manual Bicep run instead of the button, see [deploy/azure/README.md](deploy/azure/README.md).

**Docker**, no Azure account or manual setup needed:

```bash
docker compose up --build
```

This starts a local PostgreSQL container and the app together (see `docker-compose.yml`), with the schema created automatically on first start. Open `http://localhost:8080` for the live dashboard.

For a lighter local setup with no containers at all:

```bash
cd ScaleTrigger
cp appsettings.json.example appsettings.json
dotnet run
```

With the example config's default `DatabaseProvider: "Sqlite"`, this needs nothing else, just a local file (`scaletrigger.db`), created automatically on first run.

## Testing

```bash
dotnet test ScaleTrigger.Tests
```

`ScaleTrigger.Tests` is an integration suite (`WebApplicationFactory<Program>`) that hosts the real app in-process. Two layers:

- **Sqlite-backed** (no Docker, no Azure, no manual setup, needs no `appsettings.json` - a throwaway file per test class, all required settings supplied by the test factory): the core vote flow (`add` → `report` → `reset`), `LoadConfig` validation, JWT auth (login, unauthenticated/authenticated `POST /api/vote/add`), and the health endpoints.
- **MSSQL/MySQL/PostgreSQL-backed** (Testcontainers - needs Docker running locally/in CI, one real container per provider per test run): the same core flow again, but through those providers' actual stored procedures/functions (`SchemaScripts.MsSql`/`MySql`/`PostgreSql`) instead of SQLite's plain SQL - schema provisioning, `VoteAdd`/`VoteReportGet`/`DbCpuBurn`, `LoadConfig` round-tripping, and `Reset`. MSSQL's Testcontainers module only manages the SA login against `master`, so `MsSqlScaleTriggerApplicationFactory` creates a named database itself before the app connects, matching how `MsSqlRepository` is used in production (an already-existing named database, never `CREATE DATABASE` from app code).

Runs in CI (`.github/workflows/deploy-api.yml`, which has Docker available by default on `ubuntu-latest`) before every deploy.

## Azure demo infrastructure: one click, provisions and deploys everything

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fdopiskur%2FscaleTrigger%2Fmaster%2Fdeploy%2Fazure-demo-resources%2Fmain.json)

Beyond the single-App-Service Quickstart button above, this provisions the full scaling
demo environment: five Azure scaling scenarios (VM, VM Scale Set, App Service, Azure SQL
Serverless) plus a live monitoring dashboard, each running ScaleTrigger and wired to
autoscale/resize automatically under load. Click it, fill in a password (the only
required field), and deploy — no PowerShell, no follow-up script.

Expect it to take **30–40 minutes**, not the few minutes the resource list would suggest —
Log Analytics table propagation and Azure SQL Serverless provisioning dominate that time,
not anything ScaleTrigger-specific; see the linked README for the full breakdown.

See [deploy/azure-demo-resources/README.md](deploy/azure-demo-resources/README.md) for
the full walkthrough and cost breakdown.

Once it's up, [`deploy/azure-demo-resources/Run-ScalingScenarios.ps1`](deploy/azure-demo-resources/Run-ScalingScenarios.ps1)
drives load against each scaling scenario in turn and collects exactly when it scaled
and how long it took, as CSVs plus an HTML report — useful if you need to write up or
benchmark the results rather than just watch the dashboard:

```powershell
.\Run-ScalingScenarios.ps1 -Scenario All -Path .\results -AdminPassword "MyDeployPassword123!"
```

## Configuration

`appsettings.json` is gitignored (it holds connection strings and secrets); `appsettings.json.example` is the checked-in template. Copy it before running:

```bash
cp ScaleTrigger/appsettings.json.example ScaleTrigger/appsettings.json
```

Key settings:

| Setting | Purpose |
|---|---|
| `DatabaseProvider` | `"MsSql"`, `"MySql"`, `"PostgreSql"`, or `"Sqlite"`; selects the repository implementation |
| `ConnectionStrings:<Provider>` | Connection string for the selected provider (the others don't need to be valid) |
| `UseManagedIdentity` | MSSQL only. `true` authenticates via Azure Managed Identity instead of a plain-text password (see below) |
| `Auth:Enabled` | `false` (default): `POST /api/vote/add` and other admin actions need no token. `true`: they require a JWT from `POST /api/auth/login` |
| `Startup:FailFastOnDbCheck` | `false` (default): log a critical error and keep running if the database is unreachable. `true`: refuse to start |
| `Cache:SlidingExpirationMinutes` | Sliding expiration for the `GET /api/vote/report` cache entry, in minutes |
| `LoadSafety:MaxConcurrentMemoryBytes` | Ceiling (bytes, default 2 GB) on total memory reserved by in-flight `MemoryKilobytesPerVote` allocations across all requests on this instance; see the `MemoryKilobytesPerVote` note above |
| *(request timeout)* | Every request gets a fixed 300s server-side timeout (a 504 if exceeded), not configurable via `appsettings.json`. Deliberately well above a single vote's own legitimate worst case (~131s: 60s `NetworkLatencyMillisecondsPerVote` + ~30s `CpuIterationsPerVote` + ~41s of memory-ramp delay at max `MemoryKilobytesPerVote`, before `DiskWriteKilobytesPerVote`/`DbCpuIterationsPerVote` add anything), so it only cuts off a genuinely stuck request (deadlock, network partition), not a heavy load config |
| `Load:*` | Seeds the initial `LoadConfig` values (see "How it works" above); after the first run, edit these live instead |
| `Load:ConfigRefresh` | How often (seconds) a live edit to `Load:*` takes effect |
| `Load:LoadEnabled` | `0`/`1`: master switch for per-vote load and the `Vote`/`Payload` write; see "Discard backlog" in the Dashboard section below |
| `Load:CacheEnabled` | `0`/`1`: whether `GET /api/vote/report` is cached in memory; disable to benchmark database read load in isolation. Live, like the rest of `Load:*` |
| `NodeBenchmark:*` | Duration/size/repetitions for the on-demand CPU/memory/disk saturation test (`POST /api/nodebenchmark/run`), unrelated to per-vote load |
| `Jwt:Key` / `Jwt:Issuer` / `Jwt:Audience` / `Jwt:ExpirationMinutes` | JWT signing config. The default key is a placeholder; replace it if `Auth:Enabled` is ever `true` outside a throwaway environment |
| `AdminUser:Username` / `AdminUser:Password` | Login credentials for `POST /api/auth/login` (default `admin`/`admin`; this is a stress-test tool, not a production auth system, so don't reuse real credentials here) |

### Database providers

Four repository implementations exist, selected via `DatabaseProvider`: MSSQL, MySQL, PostgreSQL, and SQLite. SQLite is the odd one out: it has no stored procedure/function support, so its repository runs plain parameterized SQL directly instead of calling stored routines. Everything else (load simulation, auth, caching, the dashboard, dynamic `LoadConfig`) behaves identically regardless of provider.

`GET /api/vote/report` and `GET /api/loadconfig` retry up to 3 times (exponential backoff, 200ms base) on a provider-specific throttling/deadlock error - Azure SQL 40613/49918/49920/4060, MySQL 1205/1213, PostgreSQL `40001`/`40P01`, SQLite's "database is locked" - since this tool deliberately pushes the database toward its limits (`DbCpuIterationsPerVote`, connection churn per vote), a throttled or deadlocked response is an expected outcome there, not a first-hit failure. `POST /api/vote/add` deliberately has no retry: without idempotency protection, retrying a write after a partial success would double-count a vote.

### Logging

Serilog replaces the default plain-text console logger, writing structured JSON to the console (scraped by App Service/Container Apps/K8s log pipelines) and to a rolling daily file under `ScaleTrigger/logs/` (a local, gitignored fallback). Every request carries an `X-Correlation-Id` - taken from the incoming request header if the client sends one, otherwise generated - echoed back on the response and attached to every log line written while handling that request, including the request-completion line and, on `POST /api/vote/add`, the write's duration/outcome. Useful for tying together the log entries from hundreds of concurrent votes under load, or for pulling every log line for one failed request.

### Connecting to Azure SQL

Two authentication modes, selected via `UseManagedIdentity`:

- **`false` (default)**: plain connection string (username/password from `ConnectionStrings:MsSql`). Simple, but the password sits in the config file in plain text.
- **`true`**: Managed Identity via the `Azure.Identity` package; the app obtains a temporary access token instead. Only works inside an Azure environment (App Service, VM) with an assigned identity that has database access.

MySQL, PostgreSQL, and SQLite currently support connection string authentication only.

### JWT authentication

`Auth:Enabled = true` makes `POST /api/vote/add`, `POST /api/vote/reset`, `POST /api/loadconfig`, and `POST /api/nodebenchmark/run` require a JWT from `POST /api/auth/login`, so a load test also exercises token validation as part of the benchmark. `GET /api/vote/report`, `GET /api/loadconfig`, and `GET /api/nodebenchmark/hardware` are always anonymous.

## Schema provisioning

There is no separate SQL file to run manually. `EnsureSchemaAsync()` creates the schema (and, for MSSQL/MySQL/PostgreSQL, its stored procedures/functions) automatically at startup, but only if it doesn't already exist, so an already-provisioned database is never touched on restart. Point `ConnectionStrings:<Provider>` at an empty database (or, for SQLite, just a file path) and start the app; nothing else is required.

Creating tables/procedures requires DDL permissions on the configured database user; if the user only has DML rights, `EnsureSchemaAsync()` fails and follows `Startup:FailFastOnDbCheck` the same way a connection failure would. On PostgreSQL, provisioning also needs permission to create the `pgcrypto` extension (used for the database-side CPU burn), which most managed offerings grant via a dedicated admin role.

## API endpoints

A "vote" is just the load-generation unit: each call is a fake yes/no choice that exists to carry a configurable amount of CPU/memory/disk/network cost, not a real feature.

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/vote/add?option=yes\|no` | optional (`Auth:Enabled`) | Generates the per-vote CPU/memory/disk/network load and records the result. Add `&dbCpuBurnOnly=true` to isolate the database-side CPU burn; no row is written |
| `GET /api/vote/report` | anonymous | `{ total, payloadCount, payloadTotalBytes }`, computed by the database |
| `POST /api/vote/reset` | optional | Drops and recreates the schema; database ends up looking brand new |
| `POST /api/auth/login` | anonymous | Body `{ "username", "password" }` → `{ "token" }` |
| `GET /api/auth/status` | anonymous | `{ "authRequired": true\|false }`, reflecting `Auth:Enabled`. No side effects - used by the load-test scripts to detect whether they need to log in, instead of a probe vote |
| `GET /api/loadconfig` | anonymous | Current `Load:*` ranges as stored in the database |
| `POST /api/loadconfig` | optional | Updates one or more `Load:*` ranges live, see "How it works" |
| `GET /api/nodebenchmark/hardware` | anonymous | Detects the hosting environment (Azure App Service/Container Apps, AWS ECS/EC2, Kubernetes, generic Docker, bare metal) and reports CPU/memory/disk |
| `POST /api/nodebenchmark/run` | optional | Runs a one-off CPU/memory/disk saturation benchmark on the current node (~20s by default) |
| `GET /health/live` | anonymous | Always `200 Healthy` once the process is up - no dependency checks. Wire to a liveness/restart probe |
| `GET /health/ready` | anonymous | `200 Healthy`/`503 Unhealthy` based on a live database round-trip. Wire to a readiness probe so a node that lost its DB connection stops receiving traffic |
| `GET /metrics` | anonymous | Prometheus text format: HTTP request duration/status/count (automatic, via OpenTelemetry's ASP.NET Core instrumentation) plus `scaletrigger_vote_add_active_calls`, a gauge for how many `POST /api/vote/add` calls are in flight right now on this instance - the concurrency signal behind the `MemoryKilobytesPerVote` risk described above, otherwise unmeasured. Also polled by the dashboard's own "Request latency" card, in addition to whatever external Prometheus/Grafana setup scrapes it |

## Node benchmark: a fast load baseline

Picking a `Load:*` range by hand is guesswork: what does `CpuIterationsPerVote = 50000` even mean on this particular node? `POST /api/nodebenchmark/run` answers that in about 20 seconds instead of a trial-and-error series of load-test runs. It benchmarks the current node in isolation, no vote traffic involved, in three steps:

1. **CPU**: chained SHA-512 hashing on every logical processor at once (dedicated threads, not the thread pool, so a benchmark taken mid-load-test isn't starved by concurrent request handling) for `NodeBenchmark:CpuDurationSeconds` (default 20s); score is total hashes/sec summed across cores. Each thread runs at `ThreadPriority.Highest` to keep the OS scheduler from starving it against everything else on the box, though in a containerized environment (cgroups/CFS quota) that priority only affects scheduling within the container's own share of CPU time, not the quota itself.
2. **Memory**: repeatedly fills a `NodeBenchmark:MemoryBlockMegabytes` buffer (default 64 MB) `NodeBenchmark:MemoryRepetitions` times (default 5); score is MB/sec, from the median fill time.
3. **Disk**: repeatedly writes and deletes a `NodeBenchmark:DiskSizeMegabytes` file (default 20 MB, real I/O via `WriteThrough` + `Flush(true)`) `NodeBenchmark:DiskRepetitions` times (default 5); score is MB/sec, from the median write time.

`GET /api/nodebenchmark/hardware` (anonymous, cached after the first call) reports the detected hosting environment (Azure App Service/Container Apps, AWS ECS/EC2, Kubernetes, generic Docker, bare metal) plus processor count, total memory, and disk size, so the scores above have context without needing to check the portal.

The dashboard's "Run benchmark" button uses the CPU score specifically to suggest a ready-to-use `CpuIterationsPerVote` range for a target vote count (see "CPU calibration" below) - the fastest path from "just deployed" to "load config that actually reflects this node's capacity."

## Dashboard

A small static dashboard is served at the application's root URL (`ScaleTrigger/wwwroot/index.html`); no separate frontend project. It shows the live total vote count and payload stats (polling `GET /api/vote/report`), lets you turn the `LoadConfig` ranges up or down while traffic is running, and can trigger the node hardware benchmark. Loading the dashboard itself needs no login, but with `Auth:Enabled = true` its admin actions (saving `LoadConfig` changes, resetting the schema, running the benchmark) prompt for one on the first `401`, same as calling those endpoints directly.

A "Request latency" card renders `POST /api/vote/add`'s latency distribution as a bar chart, polled from `GET /metrics` every 5s (parsing the Prometheus text format client-side, no new backend endpoint) - the buckets are OpenTelemetry's own default boundaries (5ms to 10s), and the count in each is that bucket's cumulative value minus the previous bucket's, so the bars show requests actually falling in that range, not "at or under" it.

A one-line summary above the Configuration table ("Currently: CPU 20,000–100,000 SHA-512 iterations · Memory 1,024–4,096 KB · ...") shows what's actually in effect for the *next* vote - as opposed to the Votes card, which shows the result of load already applied. Reads the same `GET /api/loadconfig` response the table below it renders from, no extra API call, and refreshes after any save/import/preset/reset.

**Load profile presets:** "Light"/"Medium"/"Heavy" fill the Application/Database Min/Max fields below from a fixed set of values spanning quick-warm-up to clearly-heavy - still needs "Save changes" to persist, same as "Set recommended". Skips `ConfigRefresh`/`LoadEnabled`/`CacheEnabled` (Operational, not load intensity). "Heavy" is deliberately calibrated well under both each setting's own ceiling and the default `LoadSafety:MaxConcurrentMemoryBytes` budget, so it stays a safe, repeatable demo setting rather than one that risks an OOM-kill. Editing the fields directly, with or without a preset as a starting point, is the "Custom" profile - there's no separate mode to switch into.

**Export/Import LoadConfig:** "Export JSON" downloads the current saved `LoadConfig` (the same shape `GET /api/loadconfig` returns) as a file; "Import JSON" reads a file of that shape and posts it straight to `POST /api/loadconfig`, so an imported profile passes the same `SettingNames`/`MaxAllowedValues` validation as a manual save. Useful for sharing a prepared profile for a specific lecture/demo instead of retyping the same values each time.

**CPU calibration:** "Run benchmark" measures the node's raw CPU throughput (`R_total`), then suggests a `CpuIterationsPerVote` range for a target vote count `N` you set (default 100): Max = `0.8 * (R_total / N)` - 80% of the per-vote fair share of total capacity - Min = 50% of Max. "Set recommended" fills in the Min/Max fields above (still needs "Save changes" to persist).

**Discard backlog:** if `CpuIterationsPerVote` (or any other `Load:*` range) is set high enough that already-accepted `POST /api/vote/add` calls queue up faster than they can be processed, stopping the load-test script doesn't stop that backlog - the server keeps working through everything it already accepted, at full configured cost per call, until it's drained. This button flips `LoadEnabled` off, waits a few seconds, then flips it back on: while off, `POST /api/vote/add` is a fast no-op, so the queued backlog clears near-instantly instead of running its full cost. `Total votes` legitimately undercounts whatever arrived during that window - that's expected, since it's a deliberate, visible action, not silent data loss.

## Generating load

ScaleTrigger doesn't generate its own traffic; point one of these at it. All three live in `scripts/`, call `POST /api/vote/add` with `yes`/`no` chosen randomly, and log in automatically if `Auth:Enabled` is on.

| Script | When to use it |
|---|---|
| `scaleTriggerLoad_k6.js` | Default choice for most runs: ramp-up, CI-friendly pass/fail thresholds, mature reporting. |
| `scaleTriggerLoad_locust.py` | Same test, runnable locally or uploaded directly to Azure Load Testing as a Locust test. Rate is approximate, not exact, since Locust ramps by simulated users. |
| `scaleTriggerLoad.py` | Use this if you need an exact votes-per-second rate rather than an approximation. |

See the header comment in each script for usage examples and parameters.

`scaleTriggerLoad.py` and `Run-ScalingScenarios.ps1` disable TLS certificate verification unconditionally, since they specifically target the VM/VMSS demo scenarios' self-signed certificate. `scaleTriggerLoad_k6.js` and `scaleTriggerLoad_locust.py` also run against App Service/Container Apps/AKS/Azure Load Testing, which serve a real certificate, so there it's opt-in: pass `-e INSECURE_TLS=true` (k6) or set the `INSECURE_TLS=true` environment variable (Locust) only when pointing at a VM/VMSS endpoint.

While a load-test script drives the request rate, the `LoadConfig` dashboard/API drives what each request costs server-side; independent knobs, both adjustable during the same run.

## Deploying via GitHub Actions

Need to actually stand up an App Service resource first, or want a one-click deploy? See [deploy/azure/README.md](deploy/azure/README.md).

`.github/workflows/deploy-api.yml` builds and deploys `ScaleTrigger` to Azure App Service, triggered on changes within `ScaleTrigger/` or the workflow file itself.

Before running it the first time:

1. Create an Azure App Service resource (e.g. `scaletrigger-api`) with the .NET 10 runtime.
2. In `deploy-api.yml`, replace `YOUR-API-APP-SERVICE-NAME` with the actual App Service resource name.
3. Download the *Publish Profile* (Azure Portal → App Service → "Get publish profile") and store it as the `AZURE_WEBAPP_PUBLISH_PROFILE_API` secret under **Settings → Secrets and variables → Actions**.

### appsettings.json on Azure: Application Settings, not a file

Since `appsettings.json` never ships in the deployed package, every setting must be set as an **Application Setting** in the Azure Portal (App Service → Configuration), with nested keys joined by a double underscore. For example, `ConnectionStrings:MsSql` becomes `ConnectionStrings__MsSql`, `Auth:Enabled` becomes `Auth__Enabled`, `Load:CpuIterationsPerVote:Min` becomes `Load__CpuIterationsPerVote__Min`, and so on for every nested key in `appsettings.json.example`.

