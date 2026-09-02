using System.Net;
using System.Net.Http.Json;
using ScaleTrigger.Models;

namespace ScaleTrigger.Tests
{
    /// <summary>Shared test bodies for any real (non-Sqlite) provider, run once per concrete
    /// subclass against that subclass's own container-backed factory - see
    /// MySqlIntegrationTests/PostgreSqlIntegrationTests. Exercises exactly what Sqlite can't:
    /// schema provisioning and every VoteAdd/VoteReportGet/LoadConfig call going through that
    /// engine's real stored procedures/functions (SchemaScripts.MySql/PostgreSql), not the plain
    /// SQL SqliteRepository runs instead. Requires Docker.</summary>
    public abstract class ProviderIntegrationTestsBase<TFactory> : IClassFixture<TFactory>
        where TFactory : ScaleTriggerApplicationFactory
    {
        private readonly HttpClient client;

        protected ProviderIntegrationTestsBase(TFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task SchemaProvisioning_LeavesAWorkingVoteReportEndpoint()
        {
            var response = await client.GetAsync("/api/vote/report");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task VoteAdd_ThenReport_IncrementsTotalByExactlyOne()
        {
            var before = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");

            var voteResponse = await client.PostAsync("/api/vote/add?option=yes", content: null);
            Assert.Equal(HttpStatusCode.OK, voteResponse.StatusCode);

            var after = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");

            Assert.Equal((before?.Total ?? 0) + 1, after?.Total);
        }

        [Fact]
        public async Task VoteAdd_DbCpuBurnOnly_RunsTheStoredRoutineWithoutInsertingARow()
        {
            var before = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");

            var voteResponse = await client.PostAsync("/api/vote/add?option=yes&dbCpuBurnOnly=true", content: null);
            Assert.Equal(HttpStatusCode.OK, voteResponse.StatusCode);

            var after = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");

            Assert.Equal(before?.Total, after?.Total);
        }

        [Fact]
        public async Task LoadConfig_UpdateRoundTripsThroughTheDatabase()
        {
            var update = new[] { new LoadConfigSetting { SettingName = "NetworkLatencyMillisecondsPerVote", Min = 3, Max = 9 } };

            var updateResponse = await client.PostAsJsonAsync("/api/loadconfig", update);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

            var settings = await client.GetFromJsonAsync<List<LoadConfigSetting>>("/api/loadconfig");
            var updated = settings!.Single(s => s.SettingName == "NetworkLatencyMillisecondsPerVote");
            Assert.Equal(3, updated.Min);
            Assert.Equal(9, updated.Max);
        }

        [Fact]
        public async Task Reset_DropsAndRecreatesTheSchema()
        {
            await client.PostAsync("/api/vote/add?option=yes", content: null);

            var resetResponse = await client.PostAsync("/api/vote/reset", content: null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            var report = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");
            Assert.Equal(0, report?.Total);

            var settings = await client.GetFromJsonAsync<List<LoadConfigSetting>>("/api/loadconfig");
            Assert.Equal(9, settings?.Count);
        }
    }

    public class MySqlIntegrationTests : ProviderIntegrationTestsBase<MySqlScaleTriggerApplicationFactory>
    {
        public MySqlIntegrationTests(MySqlScaleTriggerApplicationFactory factory) : base(factory)
        {
        }
    }

    public class PostgreSqlIntegrationTests : ProviderIntegrationTestsBase<PostgreSqlScaleTriggerApplicationFactory>
    {
        public PostgreSqlIntegrationTests(PostgreSqlScaleTriggerApplicationFactory factory) : base(factory)
        {
        }
    }
}
