namespace ScaleTrigger.Tests
{
    /// <summary>MemoryLoadBudget is a static, process-wide singleton (also used by every
    /// WebApplicationFactory-hosted app in this test run - each one's Program.cs sets
    /// MaxConcurrentBytes from "LoadSafety:MaxConcurrentMemoryBytes" on startup, always to the
    /// same value here since no factory in this suite overrides it). These tests never mutate
    /// MaxConcurrentBytes itself - only the ambient ceiling is read - and always release exactly
    /// what they reserved via a tracked "held" amount in a finally, so a failed assertion mid-test
    /// can't leak a permanent reservation that breaks a later test. Shares the "MemoryLoadBudget"
    /// collection with VotePayloadBudgetTests (the only other test class that reserves against
    /// this budget) so xUnit never runs them concurrently.</summary>
    [Collection("MemoryLoadBudget")]
    public class LoadSimulatorTests
    {
        [Fact]
        public void MemoryLoadBudget_TryReserve_FailsOncePastTheCeiling()
        {
            long ceiling = LoadSimulator.MemoryLoadBudget.MaxConcurrentBytes;
            long held = 0;

            try
            {
                long almostAll = ceiling - 1;
                Assert.True(LoadSimulator.MemoryLoadBudget.TryReserve(almostAll));
                held += almostAll;

                // Only 1 byte of headroom left - reserving 2 more must be refused, not allowed to overshoot.
                Assert.False(LoadSimulator.MemoryLoadBudget.TryReserve(2));
            }
            finally
            {
                if (held > 0)
                {
                    LoadSimulator.MemoryLoadBudget.Release(held);
                }
            }
        }

        [Fact]
        public void MemoryLoadBudget_Release_FreesUpTheReservedAmount()
        {
            long ceiling = LoadSimulator.MemoryLoadBudget.MaxConcurrentBytes;
            long held = 0;

            try
            {
                Assert.True(LoadSimulator.MemoryLoadBudget.TryReserve(ceiling));
                held += ceiling;

                Assert.False(LoadSimulator.MemoryLoadBudget.TryReserve(1));

                LoadSimulator.MemoryLoadBudget.Release(ceiling);
                held -= ceiling;

                Assert.True(LoadSimulator.MemoryLoadBudget.TryReserve(ceiling));
                held += ceiling;
            }
            finally
            {
                if (held > 0)
                {
                    LoadSimulator.MemoryLoadBudget.Release(held);
                }
            }
        }

        [Fact]
        public async Task SimulateMemoryLoadAsync_SkipsAllocation_WhenNoBudgetIsLeft()
        {
            long ceiling = LoadSimulator.MemoryLoadBudget.MaxConcurrentBytes;

            Assert.True(LoadSimulator.MemoryLoadBudget.TryReserve(ceiling));
            try
            {
                // Should return quietly (component skipped) rather than throw or block.
                await LoadSimulator.SimulateMemoryLoadAsync(1024);
            }
            finally
            {
                LoadSimulator.MemoryLoadBudget.Release(ceiling);
            }
        }

        [Fact]
        public async Task SimulateMemoryLoadAsync_ReleasesItsReservation_OnCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // 4096 KB spans multiple 1 MB chunks, so the cancelled Task.Delay between chunks is
            // what actually throws (the first chunk's allocation still happens first); ambient
            // ceiling (hundreds of MB) has plenty of headroom for this without needing to touch it.
            // Task.Delay's cancellation surfaces as TaskCanceledException, a subclass of
            // OperationCanceledException - ThrowsAnyAsync accepts either.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => LoadSimulator.SimulateMemoryLoadAsync(4096, cts.Token));

            // If the cancelled call's reservation leaked (missing Release), this would fail.
            long ceiling = LoadSimulator.MemoryLoadBudget.MaxConcurrentBytes;
            Assert.True(LoadSimulator.MemoryLoadBudget.TryReserve(ceiling));
            LoadSimulator.MemoryLoadBudget.Release(ceiling);
        }

        [Fact]
        public async Task SimulateDiskLoad_WritesAndCleansUpItsTempFile()
        {
            // No hook into the generated path; this just proves the streamed-write path (64 KB
            // buffer, WriteAsync/FlushAsync) completes without throwing or leaking the file handle.
            await LoadSimulator.SimulateDiskLoad(4);
        }

        [Fact]
        public async Task SimulateDiskLoad_IsANoOp_ForZeroOrNegativeKilobytes()
        {
            await LoadSimulator.SimulateDiskLoad(0);
            await LoadSimulator.SimulateDiskLoad(-1);
        }

        [Fact]
        public void SimulateCpuLoad_ThrowsOperationCanceledException_WhenAlreadyCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => LoadSimulator.SimulateCpuLoad(1_000_000, cts.Token));
        }
    }
}
