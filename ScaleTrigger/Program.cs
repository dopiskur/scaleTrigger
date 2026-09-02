using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using ScaleTrigger;
using ScaleTrigger.Auth;
using ScaleTrigger.Cache;
using ScaleTrigger.HealthChecks;
using ScaleTrigger.Interfaces;
using ScaleTrigger.Middleware;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logging in place of the plain-text console logger, so log entries (this
// startup block included) are machine-parseable - console for local/dev and for any platform
// that scrapes stdout (App Service, Container Apps, K8s), file as a durable local fallback.
// Mirrors the previous "Logging:LogLevel" appsettings.json defaults (now superseded by this).
builder.Host.UseSerilog((_, loggerConfig) => loggerConfig
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new JsonFormatter())
    .WriteTo.File(new JsonFormatter(), Path.Combine(AppContext.BaseDirectory, "logs", "scaletrigger-.json"), rollingInterval: RollingInterval.Day));

builder.Services.AddControllers();

builder.Services.AddSingleton<LoadConfigCache>();
builder.Services.AddHostedService<LoadConfigRefreshService>();

// Enabled/disabled live via the "CacheEnabled" LoadConfig setting - always registered,
// MemoryCacheRepository itself checks LoadConfigCache before actually caching anything.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICache, MemoryCacheRepository>();

builder.Services.AddScoped<RepoFactory>();

// /health/live has no checks (predicate false below) - it only confirms the process is up and
// accepting requests. /health/ready gates on "ready"-tagged checks (the database round-trip) -
// App Service/K8s/Container Apps should stop routing traffic to a node that fails it.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

// /metrics (Prometheus text format): request duration/status/count from AddAspNetCoreInstrumentation
// automatically, plus ScaleTriggerMetrics' custom gauge for votes currently in flight - the
// concurrency signal MemoryLoadBudget's risk analysis has no visibility into otherwise.
// Low risk if nothing scrapes it (Docker Compose/local dev): just an extra unused endpoint.
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(ScaleTriggerMetrics.MeterName)
        .AddPrometheusExporter());

if (builder.Configuration.GetValue<long?>("LoadSafety:MaxConcurrentMemoryBytes") is { } maxConcurrentMemoryBytes)
{
    LoadSimulator.MemoryLoadBudget.MaxConcurrentBytes = maxConcurrentMemoryBytes;
}

string? jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    throw new InvalidOperationException("Jwt:Key must be configured.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OptionalJwt", policy => policy.Requirements.Add(new OptionalJwtRequirement()));
});

builder.Services.AddSingleton<IAuthorizationHandler, OptionalAuthorizationHandler>();

// A generic 30-60s timeout would cut off legitimate heavy load configs: worst case, a single
// vote can already add up to ~131s on its own (60s NetworkLatencyMillisecondsPerVote +
// ~30s CpuIterationsPerVote at max + ~41s of memory-ramp delay at max MemoryKilobytesPerVote),
// before DiskWriteKilobytesPerVote/DbCpuIterationsPerVote add anything. 300s gives that
// legitimate worst case more than double the headroom while still failing a genuinely stuck
// request (deadlock, network partition) instead of holding it open forever.
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(300) };
});

// Per-IP throttle so credential-stuffing attempts against the single admin account don't lock out other clients.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

// Warns (doesn't block startup - this is a stress-test tool, not shipped software with a
// release gate) if the placeholder Jwt:Key/AdminUser:Password from appsettings.json.example
// are still in effect, since README already calls these out as needing to change before any
// public exposure.
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupCredentialCheck");

    const string DefaultJwtKey = "abcdefghijklmnopqrstuvwxyz012345";
    const string DefaultAdminPassword = "admin";

    if (app.Configuration["Jwt:Key"] == DefaultJwtKey)
    {
        logger.LogWarning(
            "Jwt:Key is still the placeholder value from appsettings.json.example. " +
            "Replace it before exposing this instance publicly, especially with Auth:Enabled=true.");
    }

    if (app.Configuration["AdminUser:Password"] == DefaultAdminPassword)
    {
        logger.LogWarning(
            "AdminUser:Password is still the default 'admin' from appsettings.json.example. " +
            "Replace it before exposing this instance publicly, especially with Auth:Enabled=true.");
    }
}

// Fails fast (or just warns) here instead of on the first real request - see "Startup:FailFastOnDbCheck".
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupDbCheck");
    bool failFast = bool.TryParse(app.Configuration["Startup:FailFastOnDbCheck"], out bool ff) && ff;

    using var scope = app.Services.CreateScope();
    var repoFactory = scope.ServiceProvider.GetRequiredService<RepoFactory>();

    try
    {
        var repo = repoFactory.GetRepo();
        await repo.TestConnectionAsync();
        logger.LogInformation("Database connection check succeeded (DatabaseProvider={Provider}).",
            app.Configuration["DatabaseProvider"]);

        await repo.EnsureSchemaAsync();
        logger.LogInformation("Database schema check completed (DatabaseProvider={Provider}).",
            app.Configuration["DatabaseProvider"]);

        var loadDefaults = LoadConfigDefaults.ReadFrom(app.Configuration);
        await repo.LoadConfigEnsureSeededAsync(loadDefaults);

        var loadConfigCache = app.Services.GetRequiredService<LoadConfigCache>();
        loadConfigCache.Set(await repo.LoadConfigGetAsync());
        logger.LogInformation("LoadConfig ready ({Count} settings).", loadDefaults.Count);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex,
            "Database connection or schema check FAILED (DatabaseProvider={Provider}). " +
            "Check ConnectionStrings in appsettings.json, firewall rules on Azure, " +
            "whether the database is running, and whether the configured user has " +
            "permission to create tables/procedures.",
            app.Configuration["DatabaseProvider"]);

        if (failFast)
        {
            throw;
        }
    }
}

// Forces ScaleTriggerMetrics' static constructor (registers the active-vote-calls gauge) to run
// now, before the OpenTelemetry Prometheus exporter's first collection - referencing only the
// MeterName const above doesn't trigger it, since a const is inlined at compile time.
_ = ScaleTriggerMetrics.Meter;

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapPrometheusScrapingEndpoint();

app.MapControllers();

app.Run();

// Exposes the top-level statements' auto-generated Program class (internal by default) to
// ScaleTrigger.Tests, so WebApplicationFactory<Program> can host this app in-process.
public partial class Program { }
