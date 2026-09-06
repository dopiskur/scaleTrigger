using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScaleTrigger.Models;

namespace ScaleTrigger.Controllers
{
    [ApiController]
    [Route("api/vote")]
    public class VoteApiController : ControllerBase
    {
        private readonly RepoFactory repoFactory;
        private readonly IConfiguration configuration;
        private readonly LoadConfigCache loadConfigCache;
        private readonly ILogger<VoteApiController> logger;

        private const string ReportCacheKey = "VoteReportCache";

        // Prevents a cache stampede: concurrent requests that miss at the same time
        // piggy-back on one fetch instead of each hitting the database.
        private static readonly SemaphoreSlim ReportFetchLock = new(1, 1);

        public VoteApiController(RepoFactory repoFactory, IConfiguration configuration, LoadConfigCache loadConfigCache, ILogger<VoteApiController> logger)
        {
            this.repoFactory = repoFactory;
            this.configuration = configuration;
            this.loadConfigCache = loadConfigCache;
            this.logger = logger;
        }

        /// <summary>CPU/memory/disk/network load runs here, in the app; PayloadBytesPerVote/DbCpuIterationsPerVote run inside the database instead (see IRepository.VoteAddAsync). dbCpuBurnOnly=true isolates the database-side CPU cost without inserting a row; LoadEnabled=false skips everything as a fast no-op, for the dashboard's "Discard backlog".</summary>
        [HttpPost("add")]
        [Authorize(Policy = "OptionalJwt")]
        public async Task<ActionResult> VoteAdd([FromQuery] string option, [FromQuery] bool dbCpuBurnOnly = false, CancellationToken ct = default)
        {
            if (option != "yes" && option != "no")
            {
                return BadRequest("Allowed values are only 'yes' or 'no'.");
            }

            if (loadConfigCache.Get("LoadEnabled").Min <= 0)
            {
                return Ok();
            }

            ActiveVoteTracker.Enter();
            try
            {
                int cpuIterations = RandomizedLoadValue("CpuIterationsPerVote");
                int memoryKilobytes = RandomizedLoadValue("MemoryKilobytesPerVote");
                int diskWriteKilobytes = RandomizedLoadValue("DiskWriteKilobytesPerVote");
                int networkLatencyMilliseconds = RandomizedLoadValue("NetworkLatencyMillisecondsPerVote");
                int dbHashIterations = RandomizedLoadValue("DbCpuIterationsPerVote");

                // Kestrel already dispatches request handlers on a ThreadPool thread (no
                // SynchronizationContext to marshal back to, unlike classic ASP.NET), so running
                // this CPU-bound work synchronously here uses the same pool a wrapping Task.Run
                // would - the extra hop just added scheduling overhead without isolating anything.
                LoadSimulator.SimulateCpuLoad(cpuIterations, ct);
                await LoadSimulator.SimulateMemoryLoadAsync(memoryKilobytes, ct);
                await LoadSimulator.SimulateDiskLoad(diskWriteKilobytes, ct);
                await LoadSimulator.SimulateNetworkLatencyAsync(networkLatencyMilliseconds, ct);

                var repo = repoFactory.GetRepo();
                string databaseProvider = configuration["DatabaseProvider"] ?? "Sqlite";
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    if (dbCpuBurnOnly)
                    {
                        await repo.DbCpuBurnAsync(dbHashIterations, ct);
                        logger.LogDebug(
                            "VoteAdd (dbCpuBurnOnly) completed in {ElapsedMilliseconds}ms (DatabaseProvider={DatabaseProvider}, DbCpuIterations={DbCpuIterations}).",
                            stopwatch.ElapsedMilliseconds, databaseProvider, dbHashIterations);
                        return Ok();
                    }

                    int payloadBytes = RandomizedLoadValue("PayloadBytesPerVote");
                    byte[]? payload = null;

                    // Reserved against the same MemoryLoadBudget as SimulateMemoryLoadAsync: up to
                    // 10 MB (PayloadBytesPerVote's ceiling) held for the duration of the DB call was
                    // previously uncounted, so a burst of concurrent votes could blow past the
                    // configured memory ceiling through this path alone even with
                    // MemoryKilobytesPerVote at 0. If the budget is already spoken for, this vote
                    // just skips the payload - same "component skipped, rest of the vote still runs"
                    // behavior SimulateMemoryLoadAsync uses.
                    bool payloadBudgetReserved = payloadBytes > 0 && LoadSimulator.MemoryLoadBudget.TryReserve(payloadBytes);
                    if (payloadBudgetReserved)
                    {
                        payload = new byte[payloadBytes];
                        Random.Shared.NextBytes(payload);
                    }

                    try
                    {
                        await repo.VoteAddAsync(option, payload, dbHashIterations, ct);
                        logger.LogDebug(
                            "VoteAddAsync completed in {ElapsedMilliseconds}ms (DatabaseProvider={DatabaseProvider}, PayloadBytes={PayloadBytes}, DbCpuIterations={DbCpuIterations}).",
                            stopwatch.ElapsedMilliseconds, databaseProvider, payloadBytes, dbHashIterations);
                    }
                    finally
                    {
                        if (payloadBudgetReserved)
                        {
                            LoadSimulator.MemoryLoadBudget.Release(payloadBytes);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Request timeout/client disconnect, not a database failure - let it propagate
                    // so RequestTimeoutsMiddleware (or Kestrel) handles it instead of this being
                    // misreported as a 503 DB error below.
                    throw;
                }
                catch (Exception ex)
                {
                    var failureKind = repo.ClassifyException(ex);
                    logger.LogWarning(ex,
                        "VoteAdd failed after {ElapsedMilliseconds}ms (DatabaseProvider={DatabaseProvider}, DbFailureKind={DbFailureKind}).",
                        stopwatch.ElapsedMilliseconds, databaseProvider, failureKind);
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, DbErrorResponse.For(failureKind));
                }

                repoFactory.GetCache().RemoveItem(ReportCacheKey);

                return Ok();
            }
            finally
            {
                ActiveVoteTracker.Exit();
            }
        }

        [HttpGet("report")]
        [AllowAnonymous]
        public async Task<ActionResult<VoteReport>> VoteReportGet()
        {
            // With CacheEnabled off (used to benchmark database read load in isolation), skip the lock entirely - GetItem/SetItem are no-ops anyway.
            if (loadConfigCache.Get("CacheEnabled").Min <= 0)
            {
                return await FetchReportAsync();
            }

            var cached = repoFactory.GetCache().GetItem<VoteReport>(ReportCacheKey);
            if (cached != null)
            {
                return Ok(cached);
            }

            await ReportFetchLock.WaitAsync();
            try
            {
                // Re-check: another request may have already refilled the cache while this one was waiting.
                cached = repoFactory.GetCache().GetItem<VoteReport>(ReportCacheKey);
                if (cached != null)
                {
                    return Ok(cached);
                }

                return await FetchReportAsync(cacheResult: true);
            }
            finally
            {
                ReportFetchLock.Release();
            }
        }

        private async Task<ActionResult<VoteReport>> FetchReportAsync(bool cacheResult = false)
        {
            var repo = repoFactory.GetRepo();
            VoteReport report;
            try
            {
                report = await DbRetryPolicy.ExecuteReadAsync(repo, () => repo.VoteReportGetAsync());
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DbErrorResponse.For(repo.ClassifyException(ex)));
            }

            if (cacheResult)
            {
                int slidingExpiration = int.Parse(configuration["Cache:SlidingExpirationMinutes"] ?? "5");
                repoFactory.GetCache().SetItem(ReportCacheKey, report, slidingExpiration);
            }

            return Ok(report);
        }

        /// <summary>Drops and recreates the schema, so the database ends up looking brand new.</summary>
        [HttpPost("reset")]
        [Authorize(Policy = "OptionalJwt")]
        public async Task<ActionResult> Reset()
        {
            var repo = repoFactory.GetRepo();

            try
            {
                await repo.DropSchemaAsync();
                await repo.EnsureSchemaAsync();

                var defaults = LoadConfigDefaults.ReadFrom(configuration);
                LoadConfigDefaults.EnsureValid(defaults);
                await repo.LoadConfigEnsureSeededAsync(defaults);
                loadConfigCache.Set(await repo.LoadConfigGetAsync());

                repoFactory.GetCache().RemoveItem(ReportCacheKey);

                return Ok();
            }
            catch (Exception ex)
            {
                var failureKind = repo.ClassifyException(ex);
                logger.LogWarning(ex, "Reset failed (DbFailureKind={DbFailureKind}).", failureKind);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DbErrorResponse.For(failureKind));
            }
        }

        private int RandomizedLoadValue(string settingName)
        {
            var (min, max) = loadConfigCache.Get(settingName);

            return max > min ? Random.Shared.Next(min, max + 1) : min;
        }
    }
}
