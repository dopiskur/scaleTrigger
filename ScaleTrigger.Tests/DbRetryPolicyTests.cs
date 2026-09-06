using ScaleTrigger.Interfaces;
using ScaleTrigger.Models;

namespace ScaleTrigger.Tests
{
    /// <summary>DbRetryPolicy caches its Polly pipeline per (repository type, result type) - see
    /// DbRetryPolicy.cs - on the assumption that IsTransientException's behavior depends only on
    /// the repository's concrete type, never on instance state. These fakes honor that assumption
    /// (IsTransientException is a constant per type) so the shared static cache can't leak
    /// behavior from one test into another via a stale cached pipeline.</summary>
    public class DbRetryPolicyTests
    {
        [Fact]
        public async Task ExecuteReadAsync_RetriesATransientFailure_ThenReturnsTheEventualResult()
        {
            var repo = new TransientFakeRepository();

            int result = await DbRetryPolicy.ExecuteReadAsync<int>(repo, () =>
            {
                repo.Attempts++;
                if (repo.Attempts < 3)
                {
                    throw new InvalidOperationException("transient");
                }

                return Task.FromResult(42);
            });

            Assert.Equal(42, result);
            Assert.Equal(3, repo.Attempts);
        }

        [Fact]
        public async Task ExecuteReadAsync_DoesNotRetry_WhenIsTransientExceptionReturnsFalse()
        {
            var repo = new NonTransientFakeRepository();

            await Assert.ThrowsAsync<InvalidOperationException>(() => DbRetryPolicy.ExecuteReadAsync<int>(repo, () =>
            {
                repo.Attempts++;
                throw new InvalidOperationException("not transient");
            }));

            Assert.Equal(1, repo.Attempts);
        }

        [Fact]
        public async Task ExecuteReadAsync_GivesUpAfterMaxRetryAttempts_WhenAlwaysTransient()
        {
            var repo = new TransientFakeRepository();

            await Assert.ThrowsAsync<InvalidOperationException>(() => DbRetryPolicy.ExecuteReadAsync<int>(repo, () =>
            {
                repo.Attempts++;
                throw new InvalidOperationException("always transient");
            }));

            // MaxRetryAttempts = 3 in DbRetryPolicy -> 1 initial attempt + 3 retries = 4 total.
            Assert.Equal(4, repo.Attempts);
        }

        private sealed class TransientFakeRepository : IRepository
        {
            public int Attempts;

            public bool IsTransientException(Exception ex) => true;

            public Task VoteAddAsync(string option, byte[]? payload, int hashIterations, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<VoteReport> VoteReportGetAsync() => throw new NotSupportedException();
            public Task DbCpuBurnAsync(int hashIterations, CancellationToken ct = default) => throw new NotSupportedException();
            public Task TestConnectionAsync() => throw new NotSupportedException();
            public Task EnsureSchemaAsync() => throw new NotSupportedException();
            public Task DropSchemaAsync() => throw new NotSupportedException();
            public Task LoadConfigEnsureSeededAsync(IEnumerable<LoadConfigSetting> defaults) => throw new NotSupportedException();
            public Task<List<LoadConfigSetting>> LoadConfigGetAsync() => throw new NotSupportedException();
            public Task LoadConfigUpdateAsync(IEnumerable<LoadConfigSetting> settings) => throw new NotSupportedException();
            public DbFailureKind ClassifyException(Exception ex) => throw new NotSupportedException();
        }

        private sealed class NonTransientFakeRepository : IRepository
        {
            public int Attempts;

            public bool IsTransientException(Exception ex) => false;

            public Task VoteAddAsync(string option, byte[]? payload, int hashIterations, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<VoteReport> VoteReportGetAsync() => throw new NotSupportedException();
            public Task DbCpuBurnAsync(int hashIterations, CancellationToken ct = default) => throw new NotSupportedException();
            public Task TestConnectionAsync() => throw new NotSupportedException();
            public Task EnsureSchemaAsync() => throw new NotSupportedException();
            public Task DropSchemaAsync() => throw new NotSupportedException();
            public Task LoadConfigEnsureSeededAsync(IEnumerable<LoadConfigSetting> defaults) => throw new NotSupportedException();
            public Task<List<LoadConfigSetting>> LoadConfigGetAsync() => throw new NotSupportedException();
            public Task LoadConfigUpdateAsync(IEnumerable<LoadConfigSetting> settings) => throw new NotSupportedException();
            public DbFailureKind ClassifyException(Exception ex) => throw new NotSupportedException();
        }
    }
}
