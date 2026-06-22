using Serilog.Context;

namespace PesaCore.Middleware;

// ===== CORRELATION ID MIDDLEWARE — distributed tracing for banking =====
//
// A correlation ID is a unique identifier that threads through every service
// a request touches. When a transfer fails at 2am, you search your log aggregator
// (ELK, Seq, Application Insights) for that correlation ID and see EVERY log line
// from EVERY service for that specific request — API, Finacle adapter, payment
// processor, notification service — in chronological order.
//
// Without correlation IDs, debugging distributed systems is: grep through
// millions of log lines trying to match timestamps. With them: one search, full picture.
//
// How this middleware works:
//   1. Check incoming request for X-Correlation-Id header (client or upstream service sent it)
//   2. If missing, generate a new GUID
//   3. Echo the ID back on the response header (so the client can correlate their logs with ours)
//   4. Push the ID into Serilog's LogContext — EVERY log line during this request gets the ID
//
// ASP.NET Core middleware convention:
//   - Constructor takes RequestDelegate _next (the next middleware in the pipeline)
//   - InvokeAsync is called per request — do work, then await _next(context)
//   - The pipeline is the onion: each middleware wraps the next
//
// Java equivalent: Spring HandlerInterceptor or Servlet Filter + MDC.put("correlationId", id)
// Python equivalent: ASGI middleware setting request.state.correlation_id + structlog contextvars

public class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    // RequestDelegate is a Func<HttpContext, Task> — it represents the rest of the pipeline.
    // Calling await _next(context) passes the request to the next middleware.
    // If you DON'T call _next, the pipeline short-circuits (useful for auth rejection).
    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check for existing correlation ID (propagated from upstream service or client).
        // If not present, generate a new one. This is the distributed tracing handshake.
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
                            ?? Guid.NewGuid().ToString();

        // Echo back on response — client can log this and correlate their side.
        context.Response.Headers[HeaderName] = correlationId;

        // LogContext.PushProperty: adds "CorrelationId" to Serilog's ambient context.
        // The `using` ensures it's removed when the request ends (scoped to this request).
        // Every Serilog log statement inside _next(context) — including in controllers,
        // handlers, and EF Core — will automatically include { CorrelationId: "abc-123" }.
        // This is Serilog's equivalent of Java's MDC (Mapped Diagnostic Context).
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
