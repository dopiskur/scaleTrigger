using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using ScaleTrigger;
using ScaleTrigger.Auth;
using ScaleTrigger.Cache;
using ScaleTrigger.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddSingleton<LoadConfigCache>();
builder.Services.AddHostedService<LoadConfigRefreshService>();

// Enabled/disabled live via the "CacheEnabled" LoadConfig setting - always registered,
// MemoryCacheRepository itself checks LoadConfigCache before actually caching anything.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICache, MemoryCacheRepository>();

builder.Services.AddScoped<RepoFactory>();

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

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();
