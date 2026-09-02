using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ScaleTrigger.Tests
{
    /// <summary>Hosts the real app in-process against a throwaway Sqlite file (WAL mode, same as
    /// production - see SqliteRepository.CreateConnectionAsync), one per test class via
    /// IClassFixture. Load:* is seeded near-instant (tiny CpuIterationsPerVote, everything else
    /// 0) so tests run fast and don't touch disk/network. Auth:Enabled is off by default; see
    /// AuthEnabledScaleTriggerApplicationFactory for the on variant.</summary>
    public class ScaleTriggerApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"scaletrigger-tests-{Guid.NewGuid():N}.db");

        protected virtual bool AuthEnabled => false;

        /// <summary>Override point for a provider-specific subclass (see
        /// MySqlScaleTriggerApplicationFactory/PostgreSqlScaleTriggerApplicationFactory) - must
        /// stay readable when ConfigureWebHost runs, i.e. after any container the value depends
        /// on has already started (IAsyncLifetime.InitializeAsync, not the constructor).</summary>
        protected virtual string DatabaseProvider => "Sqlite";

        protected virtual string ConnectionStringKey => "ConnectionStrings:Sqlite";

        protected virtual string ConnectionStringValue => $"Data Source={dbPath}";

        public ScaleTriggerApplicationFactory()
        {
            // Program.cs reads Jwt:Key via builder.Configuration (and fails fast if it's empty)
            // BEFORE builder.Build() runs, then captures it into the AddJwtBearer options
            // lambda by closure - so it's fixed at that point, not re-read later. ConfigureWebHost
            // below (ConfigureAppConfiguration) only takes effect as part of WebApplicationFactory's
            // internal Build() call, which happens AFTER that line already ran - an override placed
            // there would sign and validate tokens with two different keys. Environment variables,
            // by contrast, are visible to WebApplication.CreateBuilder(args)'s own configuration
            // setup from the very start, so they're in place before Program.cs's first line runs.
            Environment.SetEnvironmentVariable("Jwt__Key", "integration-test-signing-key-0123456789abcdef");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "ScaleTrigger.Tests");
            Environment.SetEnvironmentVariable("Jwt__Audience", "ScaleTrigger.Tests.Clients");
            Environment.SetEnvironmentVariable("Jwt__ExpirationMinutes", "60");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabaseProvider"] = DatabaseProvider,
                    [ConnectionStringKey] = ConnectionStringValue,
                    ["Startup:FailFastOnDbCheck"] = "true",
                    ["Auth:Enabled"] = AuthEnabled.ToString(),

                    ["AdminUser:Username"] = "admin",
                    ["AdminUser:Password"] = "integration-test-password",

                    ["LoadSafety:MaxConcurrentMemoryBytes"] = "268435456", // 256 MB - plenty for MemoryKilobytesPerVote=0 below

                    // Fast and side-effect-free: real per-vote cost stays at 0 everywhere except
                    // a tiny CPU burn, so VoteAdd exercises the real code path without slowing tests.
                    ["Load:ConfigRefresh:Min"] = "1",
                    ["Load:ConfigRefresh:Max"] = "1",
                    ["Load:LoadEnabled:Min"] = "1",
                    ["Load:LoadEnabled:Max"] = "1",
                    ["Load:CacheEnabled:Min"] = "0",
                    ["Load:CacheEnabled:Max"] = "0",
                    ["Load:CpuIterationsPerVote:Min"] = "10",
                    ["Load:CpuIterationsPerVote:Max"] = "10",
                    ["Load:MemoryKilobytesPerVote:Min"] = "0",
                    ["Load:MemoryKilobytesPerVote:Max"] = "0",
                    ["Load:DiskWriteKilobytesPerVote:Min"] = "0",
                    ["Load:DiskWriteKilobytesPerVote:Max"] = "0",
                    ["Load:NetworkLatencyMillisecondsPerVote:Min"] = "0",
                    ["Load:NetworkLatencyMillisecondsPerVote:Max"] = "0",
                    ["Load:PayloadBytesPerVote:Min"] = "0",
                    ["Load:PayloadBytesPerVote:Max"] = "0",
                    ["Load:DbCpuIterationsPerVote:Min"] = "0",
                    ["Load:DbCpuIterationsPerVote:Max"] = "0",
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                // Microsoft.Data.Sqlite pools native sqlite3 handles across SqliteConnection
                // instances for performance, keyed by connection string; disposing the app's
                // DI-scoped connections (SqliteRepository already does, per call) doesn't return
                // the file handle - without this, the file stays locked and every delete below
                // silently no-ops (caught, not thrown) rather than actually cleaning up.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                TryDelete(dbPath);
                TryDelete(dbPath + "-wal");
                TryDelete(dbPath + "-shm");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup - a leftover temp file in %TEMP% isn't worth failing the test run over.
            }
        }
    }

    /// <summary>Same app, Auth:Enabled=true - for login/JWT-gated behavior that the default
    /// (auth-disabled) factory can't exercise.</summary>
    public class AuthEnabledScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory
    {
        protected override bool AuthEnabled => true;
    }
}
