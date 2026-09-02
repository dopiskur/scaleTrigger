using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScaleTrigger.Models;

namespace ScaleTrigger.Controllers
{
    [ApiController]
    [Route("api/loadconfig")]
    public class LoadConfigApiController : ControllerBase
    {
        /// <summary>Per-setting ceiling on Max, scaled to what each unit can safely allocate/block on per vote (memory/disk bytes, latency ms), not just an arbitrary shared number.</summary>
        private static readonly Dictionary<string, int> MaxAllowedValues = new()
        {
            ["CpuIterationsPerVote"] = 100_000_000,
            ["MemoryKilobytesPerVote"] = 4_194_304,
            ["DiskWriteKilobytesPerVote"] = 1_048_576,
            ["NetworkLatencyMillisecondsPerVote"] = 60_000,
            ["PayloadBytesPerVote"] = 10_485_760,
            ["DbCpuIterationsPerVote"] = 100_000_000,
            ["ConfigRefresh"] = 3600,
            ["CacheEnabled"] = 1,
            ["LoadEnabled"] = 1
        };

        private readonly RepoFactory repoFactory;
        private readonly LoadConfigCache loadConfigCache;

        public LoadConfigApiController(RepoFactory repoFactory, LoadConfigCache loadConfigCache)
        {
            this.repoFactory = repoFactory;
            this.loadConfigCache = loadConfigCache;
        }

        /// <summary>Reads straight from the database, not the cache, so it's never a tick stale.</summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<ActionResult<List<LoadConfigSetting>>> Get()
        {
            var repo = repoFactory.GetRepo();
            try
            {
                return Ok(await DbRetryPolicy.ExecuteReadAsync(repo, () => repo.LoadConfigGetAsync()));
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DbErrorResponse.For(repo.ClassifyException(ex)));
            }
        }

        /// <summary>Refreshes LoadConfigCache immediately so the update applies to the very next vote.</summary>
        [HttpPost]
        [Authorize(Policy = "OptionalJwt")]
        public async Task<ActionResult> Update([FromBody] List<LoadConfigSetting> settings)
        {
            if (settings == null || settings.Count == 0)
            {
                return BadRequest("At least one setting is required.");
            }

            foreach (var setting in settings)
            {
                if (!LoadConfigDefaults.SettingNames.Contains(setting.SettingName))
                {
                    return BadRequest($"Unknown setting name: '{setting.SettingName}'.");
                }

                if (setting.Min < 0 || setting.Max < 0)
                {
                    return BadRequest($"'{setting.SettingName}': Min and Max must not be negative.");
                }

                if (setting.Min > setting.Max)
                {
                    return BadRequest($"'{setting.SettingName}': Min must not be greater than Max.");
                }

                if (setting.Max > MaxAllowedValues[setting.SettingName])
                {
                    return BadRequest($"'{setting.SettingName}': Max must not exceed {MaxAllowedValues[setting.SettingName]}.");
                }
            }

            var repo = repoFactory.GetRepo();
            await repo.LoadConfigUpdateAsync(settings);
            loadConfigCache.Set(await repo.LoadConfigGetAsync());

            return Ok();
        }
    }
}
