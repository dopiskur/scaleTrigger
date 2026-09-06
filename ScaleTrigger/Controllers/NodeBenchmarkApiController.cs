using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScaleTrigger.Models;

namespace ScaleTrigger.Controllers
{
    [ApiController]
    [Route("api/nodebenchmark")]
    public class NodeBenchmarkApiController : ControllerBase
    {
        private readonly IConfiguration configuration;

        // A full-saturation CPU/memory/disk run measures this node in isolation - two overlapping
        // runs (a double-click, or two operators triggering one each) corrupt each other's
        // numbers and simultaneously wreck the latency of every concurrent vote on this instance.
        private static readonly SemaphoreSlim RunLock = new(1, 1);

        public NodeBenchmarkApiController(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        [HttpGet("hardware")]
        [AllowAnonymous]
        public async Task<ActionResult<NodeHardwareInfo>> GetHardware()
        {
            return Ok(await NodeBenchmark.GetHardwareInfoAsync());
        }

        /// <summary>Runs CPU, memory, then disk in sequence, offloaded to a background thread so the CPU-bound work doesn't run on an async continuation.</summary>
        [HttpPost("run")]
        [Authorize(Policy = "OptionalJwt")]
        public async Task<ActionResult<NodeBenchmarkResult>> Run()
        {
            if (!await RunLock.WaitAsync(0))
            {
                return Conflict("A benchmark run is already in progress on this node.");
            }

            try
            {
                return await RunBenchmarkAsync();
            }
            finally
            {
                RunLock.Release();
            }
        }

        private async Task<ActionResult<NodeBenchmarkResult>> RunBenchmarkAsync()
        {
            if (!int.TryParse(configuration["NodeBenchmark:CpuDurationSeconds"], out int cpuDurationSeconds))
            {
                cpuDurationSeconds = 20;
            }

            if (!int.TryParse(configuration["NodeBenchmark:MemoryBlockMegabytes"], out int memoryBlockMegabytes))
            {
                memoryBlockMegabytes = 64;
            }

            if (!int.TryParse(configuration["NodeBenchmark:MemoryRepetitions"], out int memoryRepetitions))
            {
                memoryRepetitions = 5;
            }

            if (!int.TryParse(configuration["NodeBenchmark:DiskSizeMegabytes"], out int diskSizeMegabytes))
            {
                diskSizeMegabytes = 20;
            }

            if (!int.TryParse(configuration["NodeBenchmark:DiskRepetitions"], out int diskRepetitions))
            {
                diskRepetitions = 5;
            }

            var hardware = await NodeBenchmark.GetHardwareInfoAsync();

            double cpuScore = 0, memoryScore = 0, diskScore = 0;
            await Task.Run(() =>
            {
                cpuScore = NodeBenchmark.RunCpuBenchmark(cpuDurationSeconds);
                memoryScore = NodeBenchmark.RunMemoryBenchmark(memoryBlockMegabytes, memoryRepetitions);
                diskScore = NodeBenchmark.RunDiskBenchmark(diskSizeMegabytes, diskRepetitions, hardware.Environment);
            });

            return Ok(new NodeBenchmarkResult
            {
                Hardware = hardware,
                CpuNumbersPerSecond = cpuScore,
                MemoryMbPerSecond = memoryScore,
                DiskMbPerSecond = diskScore
            });
        }
    }
}
