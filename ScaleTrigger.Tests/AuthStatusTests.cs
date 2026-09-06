using System.Net;
using System.Text.Json;

namespace ScaleTrigger.Tests
{
    /// <summary>GET /api/auth/status lets the load-test scripts detect Auth:Enabled without a
    /// side-effecting probe vote - see AuthApiController.Status.</summary>
    public class AuthStatusTests : IClassFixture<ScaleTriggerApplicationFactory>, IClassFixture<AuthEnabledScaleTriggerApplicationFactory>
    {
        private readonly HttpClient authDisabledClient;
        private readonly HttpClient authEnabledClient;

        public AuthStatusTests(ScaleTriggerApplicationFactory authDisabledFactory, AuthEnabledScaleTriggerApplicationFactory authEnabledFactory)
        {
            authDisabledClient = authDisabledFactory.CreateClient();
            authEnabledClient = authEnabledFactory.CreateClient();
        }

        [Fact]
        public async Task Status_WithAuthDisabled_ReturnsAuthRequiredFalse()
        {
            bool authRequired = await GetAuthRequiredAsync(authDisabledClient);

            Assert.False(authRequired);
        }

        [Fact]
        public async Task Status_WithAuthEnabled_ReturnsAuthRequiredTrue()
        {
            bool authRequired = await GetAuthRequiredAsync(authEnabledClient);

            Assert.True(authRequired);
        }

        [Fact]
        public async Task Status_RequiresNoAuthHeader_EvenWhenAuthIsEnabled()
        {
            var response = await authEnabledClient.GetAsync("/api/auth/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private static async Task<bool> GetAuthRequiredAsync(HttpClient client)
        {
            var response = await client.GetAsync("/api/auth/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("authRequired").GetBoolean();
        }
    }
}
