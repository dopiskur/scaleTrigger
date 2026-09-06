using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScaleTrigger.Models;

namespace ScaleTrigger.Controllers
{
    [ApiController]
    [Route("api/loadconfig")]
    public class LoadConfigApiController : ControllerBase
    {
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
                string? error = LoadConfigDefaults.Validate(setting);
                if (error != null)
                {
                    return BadRequest(error);
                }
            }

            var repo = repoFactory.GetRepo();

            try
            {
                await repo.LoadConfigUpdateAsync(settings);
                loadConfigCache.Set(await repo.LoadConfigGetAsync());
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DbErrorResponse.For(repo.ClassifyException(ex)));
            }

            return Ok();
        }
    }
}
