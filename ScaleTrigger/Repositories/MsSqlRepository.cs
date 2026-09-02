using System.Data;
using Microsoft.Data.SqlClient;
using ScaleTrigger.Azure;
using ScaleTrigger.Interfaces;
using ScaleTrigger.Models;
using ScaleTrigger.Schema;

namespace ScaleTrigger.Repositories
{
    public class MsSqlRepository : IRepository
    {
        private readonly string connectionString;
        private readonly bool useManagedIdentity;

        public MsSqlRepository(string connectionString, bool useManagedIdentity = false)
        {
            this.connectionString = connectionString;
            this.useManagedIdentity = useManagedIdentity;
        }

        private async Task<SqlConnection> CreateConnectionAsync()
        {
            var connection = new SqlConnection(connectionString);

            // Managed Identity is recommended in Azure over a plain-text password.
            if (useManagedIdentity)
            {
                connection.AccessToken = await AzureSqlAuthProvider.GetAccessTokenAsync();
            }

            await connection.OpenAsync();
            return connection;
        }

        public async Task VoteAddAsync(string option, byte[]? payload, int hashIterations)
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VoteAdd";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@Option", option);

            // AddWithValue can't infer a type from a bare DBNull.Value.
            cmd.Parameters.Add(new SqlParameter("@Payload", SqlDbType.VarBinary, -1)
            {
                Value = (object?)payload ?? DBNull.Value
            });

            cmd.Parameters.AddWithValue("@HashIterations", hashIterations);

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Calls DbCpuBurn directly; no INSERT into Vote/Payload.</summary>
        public async Task DbCpuBurnAsync(int hashIterations)
        {
            using var connection = await CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DbCpuBurn";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@Iterations", hashIterations);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>VoteReportGet reads with WITH (NOLOCK), so it doesn't wait behind a concurrent VoteAdd's lock under READ COMMITTED.</summary>
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
                checkCmd.CommandText = "SELECT OBJECT_ID('dbo.Vote', 'U')";
                if (await checkCmd.ExecuteScalarAsync() is not DBNull and not null)
                {
                    return;
                }
            }

            foreach (var batch in SchemaScripts.MsSql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task DropSchemaAsync()
        {
            using var connection = await CreateConnectionAsync();

            // Best-effort: forces out other connections so DROPs can't hang behind an
            // open transaction. Needs ALTER DATABASE rights; skipped otherwise.
            bool forcedSingleUser = false;
            try
            {
                using var singleUserCmd = connection.CreateCommand();
                singleUserCmd.CommandText = $"ALTER DATABASE [{connection.Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;";
                await singleUserCmd.ExecuteNonQueryAsync();
                forcedSingleUser = true;

                // The pool doesn't know ROLLBACK IMMEDIATE just killed its idle connections too.
                SqlConnection.ClearPool(connection);
            }
            catch
            {
                // No ALTER DATABASE permission.
            }

            try
            {
                foreach (var batch in SchemaScripts.MsSqlDrop)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = batch;
                    await cmd.ExecuteNonQueryAsync();
                }

                try
                {
                    using var shrinkCmd = connection.CreateCommand();
                    shrinkCmd.CommandText = "DBCC SHRINKDATABASE (0);";
                    await shrinkCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Shrinking is a nice-to-have.
                }
            }
            finally
            {
                // Undo SINGLE_USER even if the DROPs above failed.
                if (forcedSingleUser)
                {
                    try
                    {
                        using var multiUserCmd = connection.CreateCommand();
                        multiUserCmd.CommandText = $"ALTER DATABASE [{connection.Database}] SET MULTI_USER;";
                        await multiUserCmd.ExecuteNonQueryAsync();
                    }
                    catch
                    {
                        // Best-effort restore.
                    }
                }
            }
        }

        public async Task LoadConfigEnsureSeededAsync(IEnumerable<LoadConfigSetting> defaults)
        {
            using var connection = await CreateConnectionAsync();

            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT OBJECT_ID('dbo.LoadConfig', 'U')";
                if (await checkCmd.ExecuteScalarAsync() is DBNull or null)
                {
                    foreach (var batch in SchemaScripts.MsSqlLoadConfig)
                    {
                        using var createCmd = connection.CreateCommand();
                        createCmd.CommandText = batch;
                        await createCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM dbo.LoadConfig";
                int count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    return;
                }
            }

            foreach (var setting in defaults)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO dbo.LoadConfig (SettingName, MinValue, MaxValue) VALUES (@Name, @Min, @Max)";
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
            using var transaction = connection.BeginTransaction();

            foreach (var setting in settings)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "LoadConfigSet";
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@SettingName", setting.SettingName);
                cmd.Parameters.AddWithValue("@MinValue", setting.Min);
                cmd.Parameters.AddWithValue("@MaxValue", setting.Max);
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        /// <summary>208 = Invalid object name, 2812 = Could not find stored procedure - reads/writes go through stored procedures, so a missing schema surfaces as either depending on which is hit first.</summary>
        public DbFailureKind ClassifyException(Exception ex)
        {
            if (ex is SqlException sqlEx && (sqlEx.Number == 208 || sqlEx.Number == 2812))
            {
                return DbFailureKind.SchemaMissing;
            }

            return DbFailureKind.ConnectionFailure;
        }

        /// <summary>40613 = database unavailable (serverless auto-pause/resuming or failover), 49918/49920 = not enough resources/too many requests (resource governor), 4060 = cannot open database (can also fire transiently during serverless resume), 1205 = chosen as the deadlock victim.</summary>
        public bool IsTransientException(Exception ex) =>
            ex is SqlException sqlEx && sqlEx.Number is 40613 or 49918 or 49920 or 4060 or 1205;
    }
}
