using Npgsql;
using NpgsqlTypes;
using ScaleTrigger.Interfaces;
using ScaleTrigger.Models;
using ScaleTrigger.Schema;

namespace ScaleTrigger.Repositories
{
    public class PostgreSqlRepository : IRepository
    {
        private readonly string connectionString;

        public PostgreSqlRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        private async Task<NpgsqlConnection> CreateConnectionAsync()
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }

        public async Task VoteAddAsync(string option, byte[]? payload, int hashIterations)
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CALL vote_add(@option, @payload, @hashIterations)";
            cmd.Parameters.AddWithValue("option", option);

            // AddWithValue can't infer a type from a bare DBNull.Value.
            cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea)
            {
                Value = (object?)payload ?? DBNull.Value
            });

            cmd.Parameters.AddWithValue("hashIterations", hashIterations);

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Calls db_cpu_burn directly; no INSERT into vote/payload.</summary>
        public async Task DbCpuBurnAsync(int hashIterations)
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CALL db_cpu_burn(@iterations)";
            cmd.Parameters.AddWithValue("iterations", hashIterations);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>No NOLOCK hint needed: a plain SELECT is always an MVCC snapshot read here. Columns aliased to PascalCase for RepositoryMappers.MapVoteReport.</summary>
        public async Task<VoteReport> VoteReportGetAsync()
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT total AS \"Total\", payload_count AS \"PayloadCount\", payload_total_bytes AS \"PayloadTotalBytes\" " +
                "FROM vote_report_get()";

            using var dr = await cmd.ExecuteReaderAsync();

            return await dr.ReadAsync() ? RepositoryMappers.MapVoteReport(dr) : new VoteReport();
        }

        /// <summary>Exceptions intentionally propagate so startup logic can log a clear error.</summary>
        public async Task TestConnectionAsync()
        {
            using var connection = await CreateConnectionAsync();
        }

        public async Task EnsureSchemaAsync()
        {
            using var connection = await CreateConnectionAsync();

            using (var checkCmd = connection.CreateCommand())
            {
                // ::text avoids reading a native "regclass" value through ExecuteScalarAsync's untyped
                // object result - Npgsql (10.x here) no longer maps that internal OID type to a CLR
                // type by default, so without the cast this throws instead of returning null/a name.
                checkCmd.CommandText = "SELECT to_regclass('public.vote')::text";
                if (await checkCmd.ExecuteScalarAsync() is not DBNull and not null)
                {
                    return;
                }
            }

            foreach (var batch in SchemaScripts.PostgreSql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task DropSchemaAsync()
        {
            using var connection = await CreateConnectionAsync();

            // Best-effort: terminates other backends so DROPs can't hang behind their
            // transactions. Needs superuser or pg_signal_backend; skipped otherwise.
            try
            {
                using var terminateCmd = connection.CreateCommand();
                terminateCmd.CommandText =
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                    "WHERE datname = current_database() AND pid != pg_backend_pid();";
                await terminateCmd.ExecuteNonQueryAsync();

                // The pool doesn't know pg_terminate_backend took its idle connections too.
                NpgsqlConnection.ClearPool(connection);
            }
            catch
            {
                // Insufficient privilege to terminate other backends.
            }

            // DROP TABLE frees the file(s) immediately; nothing left for VACUUM.
            foreach (var batch in SchemaScripts.PostgreSqlDrop)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task LoadConfigEnsureSeededAsync(IEnumerable<LoadConfigSetting> defaults)
        {
            using var connection = await CreateConnectionAsync();

            using (var checkCmd = connection.CreateCommand())
            {
                // ::text - see the same cast's comment in EnsureSchemaAsync above.
                checkCmd.CommandText = "SELECT to_regclass('public.load_config')::text";
                if (await checkCmd.ExecuteScalarAsync() is DBNull or null)
                {
                    foreach (var batch in SchemaScripts.PostgreSqlLoadConfig)
                    {
                        using var createCmd = connection.CreateCommand();
                        createCmd.CommandText = batch;
                        await createCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM load_config";
                long count = (long)(await countCmd.ExecuteScalarAsync())!;
                if (count > 0)
                {
                    return;
                }
            }

            foreach (var setting in defaults)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO load_config (setting_name, min_value, max_value) VALUES (@name, @min, @max)";
                insertCmd.Parameters.AddWithValue("name", setting.SettingName);
                insertCmd.Parameters.AddWithValue("min", setting.Min);
                insertCmd.Parameters.AddWithValue("max", setting.Max);
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>Columns aliased to PascalCase for RepositoryMappers.MapLoadConfigSetting.</summary>
        public async Task<List<LoadConfigSetting>> LoadConfigGetAsync()
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT setting_name AS \"SettingName\", min_value AS \"MinValue\", max_value AS \"MaxValue\" " +
                "FROM load_config_get()";

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
            using var transaction = await connection.BeginTransactionAsync();

            foreach (var setting in settings)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "CALL load_config_set(@name, @min, @max)";
                cmd.Parameters.AddWithValue("name", setting.SettingName);
                cmd.Parameters.AddWithValue("min", setting.Min);
                cmd.Parameters.AddWithValue("max", setting.Max);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        /// <summary>42P01 = undefined_table, 42883 = undefined_function - reads/writes go through functions/procedures, so a missing schema surfaces as either depending on which is hit first.</summary>
        public DbFailureKind ClassifyException(Exception ex)
        {
            if (ex is PostgresException pgEx && (pgEx.SqlState == "42P01" || pgEx.SqlState == "42883"))
            {
                return DbFailureKind.SchemaMissing;
            }

            return DbFailureKind.ConnectionFailure;
        }

        /// <summary>40001 = serialization_failure, 40P01 = deadlock_detected - both resolve on retry once the competing transaction releases its lock.</summary>
        public bool IsTransientException(Exception ex) =>
            ex is PostgresException pgEx && pgEx.SqlState is "40001" or "40P01";
    }
}
