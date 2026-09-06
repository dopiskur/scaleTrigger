using System.Collections.Concurrent;
using Polly;
using Polly.Retry;
using ScaleTrigger.Interfaces;

namespace ScaleTrigger
{
    /// <summary>Retries a read-only DB call when IRepository.IsTransientException says the failure
    /// was a throttling response or a deadlock - an expected outcome for a tool that deliberately
    /// pushes the database toward its limits, not something the first caller should see as a 503.
    /// Deliberately not used for writes (VoteAddAsync): without idempotency protection, retrying
    /// a write after a partial success would double-count a vote, corrupting the very measurement
    /// this tool exists to produce.</summary>
    public static class DbRetryPolicy
    {
        // Polly v8 pipelines are meant to be built once and reused, not rebuilt per call - keyed by
        // (repository type, result type) since IsTransientException's behavior depends only on the
        // repository's concrete type (it's a pure function of the exception, no instance state), so
        // any instance of that type can share one cached pipeline.
        private static readonly ConcurrentDictionary<(Type RepoType, Type ResultType), object> pipelines = new();

        public static async Task<T> ExecuteReadAsync<T>(IRepository repo, Func<Task<T>> action)
        {
            var pipeline = (ResiliencePipeline<T>)pipelines.GetOrAdd((repo.GetType(), typeof(T)), _ =>
                new ResiliencePipelineBuilder<T>()
                    .AddRetry(new RetryStrategyOptions<T>
                    {
                        ShouldHandle = new PredicateBuilder<T>().Handle<Exception>(repo.IsTransientException),
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        Delay = TimeSpan.FromMilliseconds(200)
                    })
                    .Build());

            return await pipeline.ExecuteAsync(async _ => await action()).ConfigureAwait(false);
        }
    }
}
