using Microsoft.Data.Sqlite;
using ScaleTrigger.Interfaces;
using ScaleTrigger.Models;
using ScaleTrigger.Schema;

namespace ScaleTrigger.Repositories
{
    /// <summary>SQLite has no stored procedure/function support, so this runs plain parameterized SQL directly.</summary>
    public class SqliteRepository : IRepository
    {
        private readonly string connectionString;

        public SqliteRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        /// <summary>busy_timeout avoids "database is locked" under concurrent writers; WAL mode keeps readers and writers from blocking each other.</summary>
        private async Task<SqliteConnection> CreateConnectionAsync()
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout = 5000;";
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode = WAL;";
                await cmd.ExecuteNonQueryAsync();
            }

            return connection;
        }

        /// <summary>Registers LoadSimulator.HashIterations as a scalar function and calls it from SQL, so the burn is dispatched by the SQL engine like the other providers' VoteAdd/DbCpuBurn.</summary>
        private static async Task RunSysbenchCpuAsync(SqliteConnection connection, int hashIterations)
        {
            connection.CreateFunction("sysbench_cpu", (long iterations) => LoadSimulator.HashIterations(iterations));

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sysbench_cpu($hashIterations);";
            cmd.Parameters.AddWithValue("$hashIterations", hashIterations);
            await cmd.ExecuteScalarAsync();
        }

        /// <summary>Reuses the same sysbench_cpu scalar function VoteAdd calls, but does no INSERT at all.</summary>
        public async Task DbCpuBurnAsync(int hashIterations)
        {
            using var connection = await CreateConnectionAsync();

            if (hashIterations > 0)
            {
                await RunSysbenchCpuAsync(connection, hashIterations);
            }
        }

        public async Task VoteAddAsync(string option, byte[]? payload, int hashIterations)
        {
            using var connection = await CreateConnectionAsync();

            if (hashIterations > 0)
            {
                await RunSysbenchCpuAsync(connection, hashIterations);
            }

            long newIdVote;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO Vote (\"Option\") VALUES ($option) RETURNING IDVote";
                cmd.Parameters.AddWithValue("$option", option);
                newIdVote = (long)(await cmd.ExecuteScalarAsync())!;
            }

            if (payload != null)
            {
                using var payloadCmd = connection.CreateCommand();
                payloadCmd.CommandText = "INSERT INTO Payload (IDVote, Data) VALUES ($idVote, $data)";
                payloadCmd.Parameters.AddWithValue("$idVote", newIdVote);
                payloadCmd.Parameters.AddWithValue("$data", payload);
                await payloadCmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>No NOLOCK hint needed: WAL mode already lets this read run against a snapshot without waiting on a concurrent VoteAdd writer.</summary>
        public async Task<VoteReport> VoteReportGetAsync()
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT " +
                "COUNT(*) AS Total, " +
                "(SELECT COUNT(*) FROM Payload) AS PayloadCount, " +
                "(SELECT IFNULL(SUM(LENGTH(Data)), 0) FROM Payload) AS PayloadTotalBytes " +
                "FROM Vote";

            using var dr = await cmd.ExecuteReaderAsync();

            return await dr.ReadAsync() ? RepositoryMappers.MapVoteReport(dr) : new VoteReport();
        }

        /// <summary>SQLite creates the file on first connection, so this succeeds as long as the path is writable.</summary>
        public async Task TestConnectionAsync()
        {
            using var connection = await CreateConnectionAsync();
        }

        public async Task EnsureSchemaAsync()
        {
            using var connection = await CreateConnectionAsync();

            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Vote'";
                if (await checkCmd.ExecuteScalarAsync() != null)
                {
                    return;
                }
            }

            foreach (var batch in SchemaScripts.Sqlite)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task DropSchemaAsync()
        {
            using var connection = await CreateConnectionAsync();

            // No server-side connections to kill here; instead disable busy_timeout so a
            // lock held by another process fails fast (SQLITE_BUSY) instead of blocking.
            using (var busyTimeoutCmd = connection.CreateCommand())
            {
                busyTimeoutCmd.CommandText = "PRAGMA busy_timeout = 0;";
                await busyTimeoutCmd.ExecuteNonQueryAsync();
            }

            foreach (var batch in SchemaScripts.SqliteDrop)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync();
            }

            // Best-effort: the DROPs above already succeeded regardless.
            try
            {
                using var checkpointCmd = connection.CreateCommand();
                checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpointCmd.ExecuteNonQueryAsync();

                using var vacuumCmd = connection.CreateCommand();
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Shrinking is a nice-to-have.
            }
        }

        public async Task LoadConfigEnsureSeededAsync(IEnumerable<LoadConfigSetting> defaults)
        {
            using var connection = await CreateConnectionAsync();

            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'LoadConfig'";
                if (await checkCmd.ExecuteScalarAsync() == null)
                {
                    foreach (var batch in SchemaScripts.SqliteLoadConfig)
                    {
                        using var createCmd = connection.CreateCommand();
                        createCmd.CommandText = batch;
                        await createCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM LoadConfig";
                long count = (long)(await countCmd.ExecuteScalarAsync())!;
                if (count > 0)
                {
                    return;
                }
            }

            foreach (var setting in defaults)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO LoadConfig (SettingName, MinValue, MaxValue) VALUES ($name, $min, $max)";
                insertCmd.Parameters.AddWithValue("$name", setting.SettingName);
                insertCmd.Parameters.AddWithValue("$min", setting.Min);
                insertCmd.Parameters.AddWithValue("$max", setting.Max);
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        public async Task<List<LoadConfigSetting>> LoadConfigGetAsync()
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT SettingName, MinValue, MaxValue FROM LoadConfig ORDER BY SettingName";

            var settings = new List<LoadConfigSetting>();
            using var dr = await cmd.ExecuteReaderAsync();
            while (await dr.ReadAsync())
            {
                settings.Add(RepositoryMappers.MapLoadConfigSetting(dr));
            }

            return settings;
        }

        public async Task LoadConfigUpdateAsync(IEnumerable<LoadConfigSetting> settings)
        {
            using var connection = await CreateConnectionAsync();
            using var transaction = connection.BeginTransaction();

            foreach (var setting in settings)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE LoadConfig SET MinValue = $min, MaxValue = $max, DateUpdated = CURRENT_TIMESTAMP WHERE SettingName = $name";
                cmd.Parameters.AddWithValue("$name", setting.SettingName);
                cmd.Parameters.AddWithValue("$min", setting.Min);
                cmd.Parameters.AddWithValue("$max", setting.Max);
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        /// <summary>SQLite has no dedicated "no such table" error code - SQLITE_ERROR (1) is a
        /// catch-all that in principle also covers things like malformed SQL. Every statement this
        /// repository runs is a fixed string, though (never built from user input), so the only way
        /// one of them can actually raise SQLITE_ERROR is a missing table/column - a syntax error in
        /// static SQL would fail every call, not just after Reset/DropSchema. Checking the numeric
        /// code alone is therefore as precise here as message-matching, and stable across
        /// Microsoft.Data.Sqlite versions the way exact wording isn't.</summary>
        public DbFailureKind ClassifyException(Exception ex)
        {
            if (ex is SqliteException { SqliteErrorCode: 1 })
            {
                return DbFailureKind.SchemaMissing;
            }

            return DbFailureKind.ConnectionFailure;
        }

        /// <summary>SQLITE_BUSY (5) = the database file is locked by another connection's write, SQLITE_LOCKED (6) = a table is locked within the same connection's transaction handling - each connection here is short-lived and fresh (see CreateConnectionAsync), so this is contention between concurrent votes, not a stuck lock.</summary>
        public bool IsTransientException(Exception ex) =>
            ex is SqliteException sqliteEx && sqliteEx.SqliteErrorCode is 5 or 6;
    }
}
