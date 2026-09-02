using System.Net;

namespace ScaleTrigger.Tests
{
    public class HealthEndpointsTests : IClassFixture<ScaleTriggerApplicationFactory>
    {
        private readonly HttpClient client;

        public HealthEndpointsTests(ScaleTriggerApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task Live_ReturnsHealthyRegardlessOfDatabase()
        {
            var response = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Ready_ReturnsHealthyWhenDatabaseIsReachable()
        {
            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Metrics_ExposesPrometheusTextWithTheActiveVotesGauge()
        {
            var response = await client.GetAsync("/metrics");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("scaletrigger_vote_add_active_calls", body);
        }
    }
}
