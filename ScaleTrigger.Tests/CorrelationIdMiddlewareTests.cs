using System.Linq;

namespace ScaleTrigger.Tests
{
    public class CorrelationIdMiddlewareTests : IClassFixture<ScaleTriggerApplicationFactory>
    {
        private const string HeaderName = "X-Correlation-Id";
        private readonly HttpClient client;

        public CorrelationIdMiddlewareTests(ScaleTriggerApplicationFactory factory)
        {
            client = factory.CreateClient();
        }

        [Fact]
        public async Task NoRequestHeader_GeneratesAResponseCorrelationId()
        {
            var response = await client.GetAsync("/health/live");

            Assert.True(response.Headers.TryGetValues(HeaderName, out var values));
            Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
        }

        [Fact]
        public async Task ValidRequestHeader_IsEchoedBackUnchanged()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add(HeaderName, "client-supplied-id-123");

            var response = await client.SendAsync(request);

            Assert.True(response.Headers.TryGetValues(HeaderName, out var values));
            Assert.Equal("client-supplied-id-123", values!.Single());
        }

        [Fact]
        public async Task TooLongRequestHeader_IsReplacedWithAGeneratedId()
        {
            string tooLong = new string('a', 65);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add(HeaderName, tooLong);

            var response = await client.SendAsync(request);

            Assert.True(response.Headers.TryGetValues(HeaderName, out var values));
            Assert.NotEqual(tooLong, values!.Single());
        }

        [Fact]
        public async Task RequestHeaderWithDisallowedCharacters_IsReplacedWithAGeneratedId()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add(HeaderName, "not valid!");

            var response = await client.SendAsync(request);

            Assert.True(response.Headers.TryGetValues(HeaderName, out var values));
            Assert.NotEqual("not valid!", values!.Single());
        }
    }
}
