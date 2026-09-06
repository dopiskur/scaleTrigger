using System.Text.RegularExpressions;
using Serilog.Context;

namespace ScaleTrigger.Middleware
{
    /// <summary>Reads X-Correlation-Id from the incoming request if present, otherwise generates
    /// one; echoes it back on the response and pushes it into Serilog's LogContext so every log
    /// line written while handling this request carries it - useful for tying together the log
    /// entries from hundreds of concurrent VoteAdd calls under load.</summary>
    public partial class CorrelationIdMiddleware
    {
        private const string HeaderName = "X-Correlation-Id";
        private const int MaxLength = 64;
        private readonly RequestDelegate next;

        public CorrelationIdMiddleware(RequestDelegate next)
        {
            this.next = next;
        }

        [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
        private static partial Regex AllowedCorrelationId();

        public async Task InvokeAsync(HttpContext context)
        {
            // A client-supplied value is echoed straight back as a response header and written
            // into every log line for this request - restricting it to a safe charset/length
            // stops an unbounded or CR/LF-containing value from reaching either (Kestrel rejects
            // a response header containing CR/LF outright, turning an attacker-controlled header
            // into a 500 for every request that sends it).
            string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing)
                && existing.ToString() is { Length: > 0 and <= MaxLength } candidate
                && AllowedCorrelationId().IsMatch(candidate)
                ? candidate
                : Guid.NewGuid().ToString("N");

            context.Response.Headers[HeaderName] = correlationId;

            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                await next(context);
            }
        }
    }
}
