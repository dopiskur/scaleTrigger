using System.Diagnostics.Metrics;

namespace ScaleTrigger
{
    /// <summary>The app's custom OpenTelemetry Meter, registered with AddMeter(MeterName) in
    /// Program.cs so its instruments are exported at /metrics alongside the automatic
    /// AddAspNetCoreInstrumentation() ones (request duration, status codes, ...).</summary>
    public static class ScaleTriggerMetrics
    {
        public const string MeterName = "ScaleTrigger";

        public static readonly Meter Meter = new(MeterName);

        static ScaleTriggerMetrics()
        {
            Meter.CreateObservableGauge(
                "scaletrigger_vote_add_active_calls",
                () => ActiveVoteTracker.Current,
                description: "Number of POST /api/vote/add calls currently in flight on this instance.");
        }
    }
}
