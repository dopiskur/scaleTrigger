using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace ScaleTrigger.Tests
{
    /// <summary>Same idea as MySqlScaleTriggerApplicationFactory, against a real MSSQL container -
    /// exercises MsSqlRepository's stored procedures (SchemaScripts.MsSql). Unlike the
    /// MySQL/PostgreSQL builders, MsSqlBuilder has no .WithDatabase()/.WithUsername() - it only
    /// configures the SA password, and GetConnectionString() points at "master". MsSqlRepository
    /// always expects an already-named database to exist (see appsettings.json.example:
    /// Database=ScaleTriggerDb, never CREATE DATABASE from app code), so InitializeAsync creates
    /// one against master before the app connects.</summary>
    public class MsSqlScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory, IAsyncLifetime
    {
        private const string DatabaseName = "ScaleTriggerTests";

        private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ScaleTrigger!Test1")
            .Build();

        private string connectionString = string.Empty;

        protected override string DatabaseProvider => "MsSql";
        protected override string ConnectionStringKey => "ConnectionStrings:MsSql";
        protected override string ConnectionStringValue => connectionString;

        public async Task InitializeAsync()
        {
            await container.StartAsync();

            string masterConnectionString = container.GetConnectionString();

            await using (var connection = new SqlConnection(masterConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{DatabaseName}]";
                await command.ExecuteNonQueryAsync();
            }

            connectionString = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = DatabaseName
            }.ConnectionString;
        }

        async Task IAsyncLifetime.DisposeAsync() => await container.DisposeAsync();
    }

    /// <summary>Same app as ScaleTriggerApplicationFactory, against a real MySQL container
    /// (Testcontainers - requires Docker) instead of Sqlite. Exercises MySqlRepository's stored
    /// procedures (SchemaScripts.MySql), which nothing in the Sqlite-backed suite touches:
    /// SQLite has no stored procedure support, so SqliteRepository runs plain SQL instead - a
    /// genuinely different code path. The container starts once per test class (IAsyncLifetime,
    /// shared across all [Fact]s in that class via IClassFixture), not once per test.</summary>
    public class MySqlScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory, IAsyncLifetime
    {
        private readonly MySqlContainer container = new MySqlBuilder("mysql:8.0")
            .WithDatabase("scaletrigger")
            .WithUsername("scaletrigger")
            .WithPassword("scaletrigger")
            .Build();

        protected override string DatabaseProvider => "MySql";
        protected override string ConnectionStringKey => "ConnectionStrings:MySql";
        protected override string ConnectionStringValue => container.GetConnectionString();

        public Task InitializeAsync() => container.StartAsync();

        async Task IAsyncLifetime.DisposeAsync() => await container.DisposeAsync();
    }

    /// <summary>Same idea as MySqlScaleTriggerApplicationFactory, against a real PostgreSQL
    /// container - exercises PostgreSqlRepository's functions/procedures
    /// (SchemaScripts.PostgreSql). Same image family docker-compose.yml already uses locally
    /// (postgres:16-alpine).</summary>
    public class PostgreSqlScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory, IAsyncLifetime
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("scaletrigger")
            .WithUsername("scaletrigger")
            .WithPassword("scaletrigger")
            .Build();

        protected override string DatabaseProvider => "PostgreSql";
        protected override string ConnectionStringKey => "ConnectionStrings:PostgreSql";
        protected override string ConnectionStringValue => container.GetConnectionString();

        public Task InitializeAsync() => container.StartAsync();

        async Task IAsyncLifetime.DisposeAsync() => await container.DisposeAsync();
    }
}
