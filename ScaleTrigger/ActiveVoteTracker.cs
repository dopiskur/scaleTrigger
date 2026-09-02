namespace ScaleTrigger
{
    /// <summary>Tracks how many POST /api/vote/add calls are currently in flight on this instance -
    /// the concurrency half of the concurrency x randomized-size-per-request risk that
    /// LoadSimulator.MemoryLoadBudget guards against, otherwise nowhere measured. Backs the
    /// scaletrigger_vote_add_active_calls gauge exposed at /metrics.</summary>
    public static class ActiveVoteTracker
    {
        private static long activeCount;

        public static long Current => Interlocked.Read(ref activeCount);

        public static void Enter() => Interlocked.Increment(ref activeCount);

        public static void Exit() => Interlocked.Decrement(ref activeCount);
    }
}
