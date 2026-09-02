using System.Net;
using System.Net.Http.Json;
using ScaleTrigger.Models;

namespace ScaleTrigger.Tests
{
    public class LoadConfigTests : IClassFixture<ScaleTriggerApplicationFactory>
    {
        private readonly HttpClient client;

        public LoadConfigTests(ScaleTriggerApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task Get_ReturnsAllNineSeededSettings()
        {
            var settings = await client.GetFromJsonAsync<List<LoadConfigSetting>>("/api/loadconfig");

            Assert.NotNull(settings);
            Assert.Equal(9, settings!.Count);
            Assert.Contains(settings, s => s.SettingName == "CpuIterationsPerVote" && s.Min == 10 && s.Max == 10);
        }

        [Fact]
        public async Task Update_ValidRange_PersistsAndIsReflectedOnNextGet()
        {
            var update = new[] { new LoadConfigSetting { SettingName = "NetworkLatencyMillisecondsPerVote", Min = 5, Max = 15 } };

            var updateResponse = await client.PostAsJsonAsync("/api/loadconfig", update);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

            var settings = await client.GetFromJsonAsync<List<LoadConfigSetting>>("/api/loadconfig");
            var updated = settings!.Single(s => s.SettingName == "NetworkLatencyMillisecondsPerVote");
            Assert.Equal(5, updated.Min);
            Assert.Equal(15, updated.Max);
        }

        [Fact]
        public async Task Update_MinGreaterThanMax_ReturnsBadRequest()
        {
            var update = new[] { new LoadConfigSetting { SettingName = "DiskWriteKilobytesPerVote", Min = 100, Max = 1 } };

            var response = await client.PostAsJsonAsync("/api/loadconfig", update);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Update_UnknownSettingName_ReturnsBadRequest()
        {
            var update = new[] { new LoadConfigSetting { SettingName = "NotARealSetting", Min = 0, Max = 1 } };

            var response = await client.PostAsJsonAsync("/api/loadconfig", update);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Update_ExceedsMaxAllowedValue_ReturnsBadRequest()
        {
            // NetworkLatencyMillisecondsPerVote's ceiling (LoadConfigApiController.MaxAllowedValues) is 60,000.
            var update = new[] { new LoadConfigSetting { SettingName = "NetworkLatencyMillisecondsPerVote", Min = 0, Max = 70000 } };

            var response = await client.PostAsJsonAsync("/api/loadconfig", update);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Update_EmptyList_ReturnsBadRequest()
        {
            var response = await client.PostAsJsonAsync("/api/loadconfig", Array.Empty<LoadConfigSetting>());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
