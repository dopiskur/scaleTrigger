using ScaleTrigger.Models;

namespace ScaleTrigger.Interfaces
{
    public interface IRepository
    {
        /// <summary>Pass null, not an empty array, to skip the payload insert. hashIterations &gt; 0 runs a chained SHA-512 burn inside the database before the insert.</summary>
        Task VoteAddAsync(string option, byte[]? payload, int hashIterations);

        Task<VoteReport> VoteReportGetAsync();

        /// <summary>Same CPU burn as VoteAdd, without touching Vote/Payload. 0 is a no-op.</summary>
        Task DbCpuBurnAsync(int hashIterations);

        /// <summary>Opens and closes a connection without querying, so a bad config surfaces at startup, not on the first vote.</summary>
        Task TestConnectionAsync();

        /// <summary>No-op if the schema already exists.</summary>
        Task EnsureSchemaAsync();

        /// <summary>Drops Vote, Payload and LoadConfig and shrinks the freed space; best-effort force-disconnects other connections first so this can't hang behind an open transaction. Callers must follow up with EnsureSchemaAsync() and LoadConfigEnsureSeededAsync() to get a working schema back.</summary>
        Task DropSchemaAsync();

        /// <summary>Creates LoadConfig if missing and seeds <paramref name="defaults"/> only if the table is still empty, so dashboard edits survive a later startup.</summary>
        Task LoadConfigEnsureSeededAsync(IEnumerable<LoadConfigSetting> defaults);

        Task<List<LoadConfigSetting>> LoadConfigGetAsync();

        /// <summary>Settings not already in the table are silently ignored.</summary>
        Task LoadConfigUpdateAsync(IEnumerable<LoadConfigSetting> settings);

        /// <summary>Inspects an exception caught from one of the methods above and says whether it looks like a missing schema (provider-specific "no such table/procedure" error) versus anything else (connection refused, auth failure, timeout, ...).</summary>
        DbFailureKind ClassifyException(Exception ex);

        /// <summary>True for a provider-specific throttling/deadlock error worth retrying (e.g. Azure SQL resource governor, a deadlock victim, SQLite's database-is-locked) as opposed to a connection failure or missing schema that a retry won't fix. Load-generation tools that intentionally push a database toward its limits should expect these as a normal outcome, not treat the first one as fatal.</summary>
        bool IsTransientException(Exception ex);
    }
}
