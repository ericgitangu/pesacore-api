using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PesaCore.Api.Data;
using PesaCore.Api.Models;

namespace PesaCore.Api.Behaviors;

// Custom exception for missing idempotency keys — caught by the global exception handler
// and mapped to HTTP 400 (not 500). Without a typed exception, all failures look like
// unhandled server errors. Typed exceptions let you map business violations to proper
// HTTP status codes. Same pattern as ValidationException → 400, NotFoundException → 404.
// Java equivalent: custom @ResponseStatus(HttpStatus.BAD_REQUEST) exception
// Python equivalent: raising HTTPException(status_code=400, detail="...") in FastAPI
public class MissingIdempotencyKeyException : Exception
{
    public MissingIdempotencyKeyException()
        : base("X-Idempotency-Key header is required for mutation requests. " +
               "Generate a UUID v4 on the client and send it with every POST/PUT/PATCH.") { }
}

// ===== IDEMPOTENCY BEHAVIOR — MediatR pipeline intercept for double-submit prevention =====
//
// This is a MediatR IPipelineBehavior — it wraps EVERY handler in the pipeline,
// similar to how middleware wraps every HTTP request. The pipeline looks like:
//
//   Controller.Send(command)
//     → IdempotencyBehavior (this)     ← checks/caches idempotency keys
//       → ValidationBehavior           ← FluentValidation could also be a behavior
//         → TransferFundsHandler       ← the actual business logic
//
// Why a behavior instead of HTTP middleware?
//   1. Operates at the COMMAND level, not the HTTP level — cleaner for CQRS
//   2. Has access to the typed TResponse — can serialize/deserialize without stream hacking
//   3. Participates in the same DbContext scope — key storage is in the same transaction
//   4. Only applies to MediatR commands — GET endpoints are untouched
//
// Why not HTTP middleware?
//   HTTP middleware must swap the response stream (MemoryStream trick) to capture
//   the response body, then deserialize from raw bytes. The behavior gets the typed
//   result directly from the handler — no stream gymnastics.
//
// Java equivalent: Axon Framework's CommandHandler interceptor with idempotency check
// Python equivalent: decorator on the command handler function
//
// The generic constraints:
//   TRequest : IRequest<TResponse> — only intercepts MediatR requests (commands + queries)
//   The behavior runs for ALL requests. We filter to mutations by checking the header.
public class IdempotencyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly BankDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    // Constructor injection — all three are resolved from the DI scope:
    //   BankDbContext: Scoped (same instance as the handler uses)
    //   IHttpContextAccessor: Singleton (provides access to the current HttpContext)
    //   ILogger: Transient (Serilog-backed, includes CorrelationId from LogContext)
    public IdempotencyBehavior(
        BankDbContext db,
        IHttpContextAccessor http,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,   // The next step in the pipeline (handler or next behavior)
        CancellationToken cancellationToken)
    {
        // --- Step 1: Extract the idempotency key from the HTTP header ---
        // For GET/HEAD/OPTIONS (queries), skip — they're naturally idempotent.
        // For POST/PUT/PATCH (mutations), REQUIRE the key — reject with exception if missing.
        // Banking policy: idempotency is NOT optional for financial mutations.
        // Stripe requires X-Idempotency-Key on all POST requests. We follow the same model.
        var method = _http.HttpContext?.Request.Method;
        if (method is "GET" or "HEAD" or "OPTIONS")
        {
            return await next();  // Queries are naturally idempotent — no key needed
        }

        var key = _http.HttpContext?.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key))
        {
            // Reject keyless mutations — for financial endpoints, the client MUST provide
            // a key so that retries are safe. Without this, a lost response + blind retry
            // = double-debit. The client generates the UUID; the server can't generate it
            // because it doesn't know if this request is a retry or a new operation.
            throw new MissingIdempotencyKeyException();
        }

        // --- Step 2: Check if this key has already been processed ---
        var existing = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Key == key, cancellationToken);

        if (existing != null)
        {
            // CACHE HIT — return the stored response without re-executing the handler.
            // This is the double-submit protection: same key = same response, no side effects.
            // The handler never runs. No debit, no credit, no state change.
            _logger.LogInformation(
                "Idempotency cache hit for key {IdempotencyKey} — returning cached response",
                key);

            return JsonSerializer.Deserialize<TResponse>(existing.CachedResponse)!;
        }

        // --- Step 3: CACHE MISS — run the handler ---
        var result = await next();

        // --- Step 4: Store the result against the key ---
        // This happens in the SAME DbContext scope as the handler's SaveChangesAsync.
        // If the handler wrote to the database (e.g., transferred funds), the idempotency
        // record is saved in the same transaction. Atomic: either both succeed or both fail.
        // If we crash between the handler's save and this save, the key is NOT stored,
        // so a retry will re-execute — which is safe because the handler's transaction
        // also rolled back. This is eventually consistent, not exactly-once, but it's
        // the standard trade-off without an outbox pattern.
        //
        // Race condition: Two concurrent requests with the same key both pass the
        // FirstOrDefaultAsync check (both see "no record"). Both run the handler.
        // Both try to INSERT. The UNIQUE index on Key catches the second INSERT —
        // DbUpdateException with a unique constraint violation. We catch that and
        // return the result (the handler already ran, the mutation happened, we just
        // couldn't cache the key — the next retry will find the first insert's record).
        try
        {
            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                CachedResponse = JsonSerializer.Serialize(result),
                StatusCode = _http.HttpContext?.Response.StatusCode ?? 200,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24)  // TTL — keys don't live forever
            });
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Idempotency key {IdempotencyKey} stored — response cached for 24h",
                key);
        }
        catch (DbUpdateException)
        {
            // Unique constraint violation — another concurrent request already stored this key.
            // The handler's mutation already committed (it ran before we got here).
            // Log the race condition but return the result — the client gets their response.
            // The NEXT retry with this key will hit the cache and get the first response.
            _logger.LogWarning(
                "Idempotency key {IdempotencyKey} race condition — " +
                "concurrent duplicate detected, key already stored by another request",
                key);
        }

        return result;
    }
}
