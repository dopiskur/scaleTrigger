using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ScaleTrigger.Tests
{
    /// <summary>Covers the two startup-time config checks that must fail unconditionally, not just
    /// when "Startup:FailFastOnDbCheck" happens to be on - see Program.cs.</summary>
    public class StartupValidationTests
    {
        [Fact]
        public void InvalidLoadConfigSeed_FailsStartup_EvenWhenFailFastOnDbCheckIsFalse()
        {
            using var factory = new InvalidLoadConfigScaleTriggerApplicationFactory();

            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        }

        [Fact]
        public void InvalidKnownProxiesEntry_FailsStartupWithAClearError()
        {
            using var factory = new InvalidKnownProxiesScaleTriggerApplicationFactory();

            var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Exception root = thrown;
            while (root.InnerException != null)
            {
                root = root.InnerException;
            }

            Assert.Contains("KnownProxies", root.Message);
        }

        private sealed class InvalidLoadConfigScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Explicitly off, plus an invalid seed (Min > Max) - proves config
                        // validation fails startup regardless of this DB-availability flag.
                        ["Startup:FailFastOnDbCheck"] = "false",
                        ["Load:CpuIterationsPerVote:Min"] = "100",
                        ["Load:CpuIterationsPerVote:Max"] = "10",
                    });
                });
            }
        }

        private sealed class InvalidKnownProxiesScaleTriggerApplicationFactory : ScaleTriggerApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ForwardedHeaders:KnownProxies"] = "not-an-ip",
                    });
                });
            }
        }
    }
}
