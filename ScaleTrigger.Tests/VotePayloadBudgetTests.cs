using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ScaleTrigger.Tests
{
    /// <summary>Regression coverage for VoteAdd's PayloadBytesPerVote allocation going through
    /// LoadSimulator.MemoryLoadBudget - previously that byte array was allocated unconditionally,
    /// uncounted by the same budget SimulateMemoryLoadAsync respects. Shares the "MemoryLoadBudget"
    /// collection with LoadSimulatorTests so xUnit never runs them concurrently against the same
    /// static budget.</summary>
    [Collection("MemoryLoadBudget")]
    public class VotePayloadBudgetTests : IClassFixture<VotePayloadBudgetTests.PayloadEnabledApplicationFactory>
    {
        private readonly HttpClient client;

        public VotePayloadBudgetTests(PayloadEnabledApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task VoteAdd_ReleasesThePayloadReservation_AfterTheVoteCompletes()
        {
            var response = await client.PostAsync("/api/vote/add?option=yes", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // If VoteAdd's payload reservation leaked (missing Release), reserving the entire
            // current ceiling right after would fail because part of it is still counted as in use.
            long ceiling = LoadSimulator.MemoryLoadBudget.MaxConcurrentBytes;
            Assert.True(LoadSimulator.MemoryLoadBudget.TryReserve(ceiling));
            LoadSimulator.MemoryLoadBudget.Release(ceiling);
        }

        public class PayloadEnabledApplicationFactory : ScaleTriggerApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Load:PayloadBytesPerVote:Min"] = "2048",
                        ["Load:PayloadBytesPerVote:Max"] = "2048",
                    });
                });
            }
        }
    }
}
