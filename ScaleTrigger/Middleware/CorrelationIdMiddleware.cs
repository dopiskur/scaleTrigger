using Serilog.Context;

namespace ScaleTrigger.Middleware
{
    /// <summary>Reads X-Correlation-Id from the incoming request if present, otherwise generates
    /// one; echoes it back on the response and pushes it into Serilog's LogContext so every log
    /// line written while handling this request carries it - useful for tying together the log
    /// entries from hundreds of concurrent VoteAdd calls under load.</summary>
    public class CorrelationIdMiddleware
    {
        private const string HeaderName = "X-Correlation-Id";
        private readonly RequestDelegate next;

        public CorrelationIdMiddleware(RequestDelegate next)
        {
            this.next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing) && !string.IsNullOrWhiteSpace(existing)
                ? existing.ToString()
                : Guid.NewGuid().ToString("N");

            context.Response.Headers[HeaderName] = correlationId;

            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                await next(context);
            }
        }
    }
}
