using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScaleTrigger.Tests
{
    public class AuthTests : IClassFixture<AuthEnabledScaleTriggerApplicationFactory>
    {
        private const string AdminUsername = "admin";
        private const string AdminPassword = "integration-test-password"; // matches ScaleTriggerApplicationFactory's AdminUser:Password

        private readonly HttpClient client;

        public AuthTests(AuthEnabledScaleTriggerApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task VoteAdd_WithoutAToken_ReturnsUnauthorized()
        {
            var response = await client.PostAsync("/api/vote/add?option=yes", content: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Report_RemainsAnonymous_EvenWithAuthEnabled()
        {
            var response = await client.GetAsync("/api/vote/report");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Login_WithValidCredentials_ReturnsAToken()
        {
            string token = await LoginAsync();

            Assert.False(string.IsNullOrWhiteSpace(token));
        }

        [Fact]
        public async Task Login_WithWrongPassword_ReturnsUnauthorized()
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUsername, password = "not-the-right-password" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task VoteAdd_WithAValidToken_Succeeds()
        {
            string token = await LoginAsync();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/vote/add?option=yes");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private async Task<string> LoginAsync()
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUsername, password = AdminPassword });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("token").GetString() ?? string.Empty;
        }
    }
}
