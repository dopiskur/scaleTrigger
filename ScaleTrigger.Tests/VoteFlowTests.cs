using System.Net;
using System.Net.Http.Json;
using ScaleTrigger.Models;

namespace ScaleTrigger.Tests
{
    public class VoteFlowTests : IClassFixture<ScaleTriggerApplicationFactory>
    {
        private readonly HttpClient client;

        public VoteFlowTests(ScaleTriggerApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task VoteAdd_RejectsAnOptionOtherThanYesOrNo()
        {
            var response = await client.PostAsync("/api/vote/add?option=maybe", content: null);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        public async Task VoteAdd_DbCpuBurnOnly_DoesNotChangeTheVoteTotal()
        {
            var before = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");

            var voteResponse = await client.PostAsync("/api/vote/add?option=yes&dbCpuBurnOnly=true", content: null);
            Assert.Equal(HttpStatusCode.OK, voteResponse.StatusCode);

            var after = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");

            Assert.Equal(before?.Total, after?.Total);
        }

        [Fact]
        public async Task Reset_ClearsVotesAndReseedsLoadConfig()
        {
            await client.PostAsync("/api/vote/add?option=yes", content: null);
            await client.PostAsync("/api/vote/add?option=no", content: null);

            var resetResponse = await client.PostAsync("/api/vote/reset", content: null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            var report = await client.GetFromJsonAsync<VoteReport>("/api/vote/report");
            Assert.Equal(0, report?.Total);

            var settings = await client.GetFromJsonAsync<List<LoadConfigSetting>>("/api/loadconfig");
            Assert.Equal(9, settings?.Count);
        }
    }
}
