using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ScaleTrigger.HealthChecks
{
    /// <summary>Backs /health/ready - the same TestConnectionAsync round-trip Program.cs runs
    /// at startup, so a node that has lost its database connection stops receiving traffic
    /// instead of accepting requests it can't fulfill.</summary>
    public class DatabaseHealthCheck : IHealthCheck
    {
        private readonly RepoFactory repoFactory;

        public DatabaseHealthCheck(RepoFactory repoFactory)
        {
            this.repoFactory = repoFactory;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await repoFactory.GetRepo().TestConnectionAsync();
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Database connection check failed.", ex);
            }
        }
    }
}
