using System.Security.Cryptography;

namespace ScaleTrigger
{
    public static class LoadSimulator
    {
        private static readonly Lazy<Task<string>> DiskLoadDirectory = new(ResolveDiskLoadDirectoryAsync);

        /// <summary>Kubernetes commonly mounts /tmp as a memory-backed emptyDir, so disk load there targets the app's working directory instead of Path.GetTempPath().</summary>
        private static async Task<string> ResolveDiskLoadDirectoryAsync()
        {
            var hardware = await NodeBenchmark.GetHardwareInfoAsync();

            string path = hardware.Environment == "Kubernetes"
                ? Path.Combine(AppContext.BaseDirectory, "diskload")
                : Path.Combine(Path.GetTempPath(), "ScaleTrigger", "diskload");

            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Chained SHA-512: each hash's output feeds the next, so the JIT can't fold the loop away.</summary>
        public static void SimulateCpuLoad(int iterations, CancellationToken ct = default)
        {
            if (iterations <= 0)
            {
                return;
            }

            byte[] result = HashIterations(iterations, ct);
            GC.KeepAlive(result);
        }

        /// <summary>Shared by SimulateCpuLoad and SqliteRepository's sysbench_cpu scalar function
        /// (which always calls with the default, uncancellable token - only the app-side CPU load
        /// has a request timeout to observe).</summary>
        internal static byte[] HashIterations(long iterations, CancellationToken ct = default)
        {
            const long checkEvery = 100_000;

            byte[] buffer = new byte[64];
            Random.Shared.NextBytes(buffer);

            for (long i = 0; i < iterations; i++)
            {
                buffer = SHA512.HashData(buffer);

                if (i % checkEvery == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
            }

            return buffer;
        }

        /// <summary>Tracks total bytes currently allocated by in-flight SimulateMemoryLoadAsync
        /// calls on this instance, so a burst of concurrent requests can't collectively
        /// exceed a configured ceiling even though no single request's Min/Max looks dangerous
        /// on its own. Complementary to the per-request ramp below, not a replacement for it.</summary>
        public static class MemoryLoadBudget
        {
            private static long currentlyAllocatedBytes;

            public static long MaxConcurrentBytes { get; set; } = 2L * 1024 * 1024 * 1024;

            public static bool TryReserve(long bytes)
            {
                long updated = Interlocked.Add(ref currentlyAllocatedBytes, bytes);
                if (updated <= MaxConcurrentBytes)
                {
                    return true;
                }

                Interlocked.Add(ref currentlyAllocatedBytes, -bytes);
                return false;
            }

            public static void Release(long bytes) => Interlocked.Add(ref currentlyAllocatedBytes, -bytes);
        }

        /// <summary>Allocates in chunks with a small delay between them instead of one large
        /// synchronous allocation, so memory pressure ramps up visibly (same shape as CPU/network
        /// load) instead of hitting the OS/cgroup memory limit as an instant spike that can trigger
        /// an OOM-kill before autoscale metrics sample it. Reserves against MemoryLoadBudget first;
        /// if the instance-wide ceiling is already spoken for, this component is skipped for this
        /// vote while CPU/disk/network/DB load still run normally.</summary>
        public static async Task SimulateMemoryLoadAsync(int kilobytes, CancellationToken ct = default)
        {
            if (kilobytes <= 0)
            {
                return;
            }

            const int chunkSizeBytes = 1_048_576; // 1 MB per chunk
            const int delayPerChunkMs = 10;

            long totalBytes = (long)kilobytes * 1024;

            if (!MemoryLoadBudget.TryReserve(totalBytes))
            {
                return;
            }

            try
            {
                long remaining = totalBytes;
                var chunks = new List<byte[]>();

                while (remaining > 0)
                {
                    int thisChunk = (int)Math.Min(chunkSizeBytes, remaining);
                    var buffer = new byte[thisChunk];
                    Random.Shared.NextBytes(buffer); // touch pages so they're actually committed
                    chunks.Add(buffer);
                    remaining -= thisChunk;

                    if (remaining > 0)
                    {
                        await Task.Delay(delayPerChunkMs, ct);
                    }
                }

                GC.KeepAlive(chunks);
            }
            finally
            {
                // On cancellation mid-ramp, the exception propagates past this finally (chunks
                // already allocated get GC'd normally) so the caller's timeout actually aborts
                // the request instead of being swallowed here while the rest of the vote keeps running.
                MemoryLoadBudget.Release(totalBytes);
            }
        }

        /// <summary>Each call uses its own uniquely-named file so concurrent votes don't serialize on
        /// a shared one. Writes from a small reusable buffer refilled each chunk, rather than
        /// allocating the full file size up front, so a large DiskWriteKilobytesPerVote produces disk
        /// I/O pressure instead of a memory spike - and so the amount actually held in memory at once
        /// is bounded regardless of how large the configured value is.</summary>
        public static async Task SimulateDiskLoad(int kilobytes, CancellationToken ct = default)
        {
            if (kilobytes <= 0)
            {
                return;
            }

            const int chunkSizeBytes = 64 * 1024;

            long remaining = (long)kilobytes * 1024;
            byte[] buffer = new byte[Math.Min(chunkSizeBytes, remaining)];

            string directory = await DiskLoadDirectory.Value;
            string path = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");

            try
            {
                using var stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);

                while (remaining > 0)
                {
                    int thisChunk = (int)Math.Min(buffer.Length, remaining);
                    Random.Shared.NextBytes(buffer.AsSpan(0, thisChunk));
                    await stream.WriteAsync(buffer.AsMemory(0, thisChunk), ct);
                    remaining -= thisChunk;
                }

                await stream.FlushAsync(ct);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Task.Delay, not a blocking sleep, so the request thread is freed for the wait.</summary>
        public static Task SimulateNetworkLatencyAsync(int milliseconds, CancellationToken ct = default)
        {
            return milliseconds > 0 ? Task.Delay(milliseconds, ct) : Task.CompletedTask;
        }
    }
}
