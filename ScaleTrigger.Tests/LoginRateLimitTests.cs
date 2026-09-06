using System.Net;
using System.Net.Http.Json;

namespace ScaleTrigger.Tests
{
    /// <summary>Own dedicated factory instance (not shared with AuthTests) so this test's five
    /// login attempts can't count against - or be diluted by - login calls made by tests in a
    /// different test class against a different app instance's rate limiter state.</summary>
    public class LoginRateLimitTests : IClassFixture<ScaleTriggerApplicationFactory>
    {
        private readonly HttpClient client;

        public LoginRateLimitTests(ScaleTriggerApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task Login_SixthAttemptWithinAMinute_ReturnsTooManyRequests()
        {
            for (int i = 0; i < 5; i++)
            {
                var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong-password" });
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            var sixthResponse = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong-password" });

            Assert.Equal(HttpStatusCode.TooManyRequests, sixthResponse.StatusCode);
        }
    }
}
