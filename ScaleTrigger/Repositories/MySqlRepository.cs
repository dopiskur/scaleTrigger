using MySqlConnector;
using ScaleTrigger.Interfaces;
using ScaleTrigger.Models;
using ScaleTrigger.Schema;

namespace ScaleTrigger.Repositories
{
    public class MySqlRepository : IRepository
    {
        private readonly string connectionString;

        public MySqlRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        private async Task<MySqlConnection> CreateConnectionAsync(CancellationToken ct = default)
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }

        public async Task VoteAddAsync(string option, byte[]? payload, int hashIterations, CancellationToken ct = default)
        {
            using var connection = await CreateConnectionAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VoteAdd";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("pOption", option);

            // AddWithValue can't infer a type from a bare DBNull.Value.
            cmd.Parameters.Add(new MySqlParameter("pPayload", MySqlDbType.LongBlob)
            {
                Value = (object?)payload ?? DBNull.Value
            });

            cmd.Parameters.AddWithValue("pHashIterations", hashIterations);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>Calls DbCpuBurn directly; no INSERT into Vote/Payload.</summary>
        public async Task DbCpuBurnAsync(int hashIterations, CancellationToken ct = default)
        {
            using var connection = await CreateConnectionAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DbCpuBurn";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("pIterations", hashIterations);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>No NOLOCK hint needed: InnoDB's plain SELECT is already a non-locking MVCC read.</summary>
        public async Task<VoteReport> VoteReportGetAsync()
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VoteReportGet";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;

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
                checkCmd.CommandText =
                    "SELECT COUNT(*) FROM information_schema.tables " +
                    "WHERE table_schema = DATABASE() AND table_name = 'Vote'";
                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    return;
                }
            }

            foreach (var batch in SchemaScripts.MySql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task DropSchemaAsync()
        {
            using var connection = await CreateConnectionAsync();

            // Best-effort: kills other connections so DROPs can't hang behind their
            // transactions. Needs PROCESS + CONNECTION_ADMIN/SUPER; skipped otherwise.
            try
            {
                var connectionIdsToKill = new List<long>();
                using (var listCmd = connection.CreateCommand())
                {
                    listCmd.CommandText =
                        "SELECT id FROM information_schema.processlist " +
                        "WHERE db = DATABASE() AND id != CONNECTION_ID()";
                    using var reader = await listCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        connectionIdsToKill.Add(reader.GetInt64(0));
                    }
                }

                foreach (var id in connectionIdsToKill)
                {
                    try
                    {
                        using var killCmd = connection.CreateCommand();
                        killCmd.CommandText = $"KILL {id};";
                        await killCmd.ExecuteNonQueryAsync();
                    }
                    catch
                    {
                        // Already closed, or not ours to kill.
                    }
                }

                if (connectionIdsToKill.Count > 0)
                {
                    // The pool doesn't know those KILLs took its idle connections too.
                    await MySqlConnection.ClearPoolAsync(connection);
                }
            }
            catch
            {
                // No PROCESS permission to list other connections.
            }

            // InnoDB frees each table's .ibd file as part of DROP TABLE itself; no separate shrink step.
            foreach (var batch in SchemaScripts.MySqlDrop)
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
                checkCmd.CommandText =
                    "SELECT COUNT(*) FROM information_schema.tables " +
                    "WHERE table_schema = DATABASE() AND table_name = 'LoadConfig'";
                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    foreach (var batch in SchemaScripts.MySqlLoadConfig)
                    {
                        using var createCmd = connection.CreateCommand();
                        createCmd.CommandText = batch;
                        await createCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM `LoadConfig`";
                var rowCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                if (rowCount > 0)
                {
                    return;
                }
            }

            foreach (var setting in defaults)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO `LoadConfig` (`SettingName`, `MinValue`, `MaxValue`) VALUES (@Name, @Min, @Max)";
                insertCmd.Parameters.AddWithValue("@Name", setting.SettingName);
                insertCmd.Parameters.AddWithValue("@Min", setting.Min);
                insertCmd.Parameters.AddWithValue("@Max", setting.Max);
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        public async Task<List<LoadConfigSetting>> LoadConfigGetAsync()
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "LoadConfigGet";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;

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
                cmd.CommandText = "LoadConfigSet";
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("pSettingName", setting.SettingName);
                cmd.Parameters.AddWithValue("pMinValue", setting.Min);
                cmd.Parameters.AddWithValue("pMaxValue", setting.Max);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        /// <summary>1146 = ER_NO_SUCH_TABLE, 1305 = ER_SP_DOES_NOT_EXIST - reads/writes go through stored procedures, so a missing schema surfaces as either depending on which is hit first.</summary>
        public DbFailureKind ClassifyException(Exception ex)
        {
            if (ex is MySqlException mysqlEx && (mysqlEx.Number == 1146 || mysqlEx.Number == 1305))
            {
                return DbFailureKind.SchemaMissing;
            }

            return DbFailureKind.ConnectionFailure;
        }

        /// <summary>1205 = ER_LOCK_WAIT_TIMEOUT, 1213 = ER_LOCK_DEADLOCK - both resolve on retry once the competing transaction releases its lock.</summary>
        public bool IsTransientException(Exception ex) =>
            ex is MySqlException mysqlEx && mysqlEx.Number is 1205 or 1213;
    }
}
