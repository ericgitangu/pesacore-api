using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using PesaCore.Observability;

namespace PesaCore.Behaviors;

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
               "Generate a UUID v4 on the client and send it with every POST/PUT/PATCH.")
    { }
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
//  — WHY THE STORE MOVED FROM THE DB TO IDistributedCache:
//   The old design stored keys in the SAME SQLite DbContext (one transaction with the
//   handler). That is CORRECT only on a single always-on box. PesaCore's deploy model is
//   scale-to-zero, multi-instance Cloud Run: an in-process / per-container store means
//   instance B never sees instance A's keys, so a retried transfer double-executes. The
//   fix is SHARED EPHEMERAL STATE — Upstash Redis behind IDistributedCache. The vendor is
//   a deployment choice: no Redis configured → AddDistributedMemoryCache (graceful
//   degradation; still correct on a single instance, e.g. local dev + tests).
//
// Why a behavior instead of HTTP middleware?
//   1. Operates at the COMMAND level, not the HTTP level — cleaner for CQRS
//   2. Has access to the typed TResponse — can serialize/deserialize without stream hacking
//   3. Only applies to MediatR commands — GET endpoints are untouched
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
    private const string KeyPrefix = "idem:";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly IDistributedCache _cache;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;
    private readonly IPesaCoreMetrics _metrics;

    // Constructor injection — resolved from the DI scope:
    //   IDistributedCache: the shared idempotency store (Upstash Redis in prod,
    //     in-memory distributed cache in local dev / tests). Required — the whole
    //     point of  is that this store is no longer in-process.
    //   IHttpContextAccessor: Singleton (provides access to the current HttpContext)
    //   ILogger: Transient (Serilog-backed, includes CorrelationId from LogContext)
    // metrics is optional so unit tests can `new` the behavior without a Meter;
    // the DI container always supplies the real PesaCoreMetrics in production.
    public IdempotencyBehavior(
        IDistributedCache cache,
        IHttpContextAccessor http,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger,
        IPesaCoreMetrics? metrics = null)
    {
        _cache = cache;
        _http = http;
        _logger = logger;
        _metrics = metrics ?? NoopPesaCoreMetrics.Instance;
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

        var headerKey = _http.HttpContext?.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(headerKey))
        {
            // Reject keyless mutations — for financial endpoints, the client MUST provide
            // a key so that retries are safe. Without this, a lost response + blind retry
            // = double-debit. The client generates the UUID; the server can't generate it
            // because it doesn't know if this request is a retry or a new operation.
            throw new MissingIdempotencyKeyException();
        }

        // Namespace the cache key by the command TYPE as well as the client key.
        // Without the type segment, the SAME X-Idempotency-Key reused across two
        // different endpoints (e.g. POST /transfer then POST /accounts) collides on
        // one cache entry — the second request would get the first's cached payload
        // cross-deserialized into the wrong TResponse (a TransferResult read back as a
        // CreateAccountResult, or a silent type mismatch). Scoping by TRequest.Name
        // isolates each operation's idempotency space. Clients are still expected to
        // mint a fresh UUID per logical operation; this is defence-in-depth.
        var cacheKey = $"{KeyPrefix}{typeof(TRequest).Name}:{headerKey}";

        // --- Step 2: Check if this key has already been processed ---
        // Shared lookup across all instances (Redis) — this is the correctness fix:
        // instance B now sees the key instance A stored.
        // The READ is best-effort, mirroring the write below: a Redis outage must
        // DEGRADE (run the handler) rather than turn every mutation into a 500. Without
        // the try/catch, an unreachable cache on the lookup path fails the whole request
        // even though the handler could have served it. The trade-off when degraded: we
        // lose dedupe protection until the cache recovers (a retry may re-execute), which
        // is the same eventual-consistency window the write-side outage already accepts.
        string? cached;
        try
        {
            cached = await _cache.GetStringAsync(cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Idempotency cache lookup failed for key {IdempotencyKey} — " +
                "proceeding without dedupe; a retry may re-execute until the cache recovers",
                headerKey);
            cached = null;
        }

        if (cached is not null)
        {
            // CACHE HIT — return the stored response without re-executing the handler.
            // This is the double-submit protection: same key = same response, no side effects.
            _logger.LogInformation(
                "Idempotency cache hit for key {IdempotencyKey} — returning cached response",
                headerKey);

            _metrics.RecordIdempotencyHit();
            return JsonSerializer.Deserialize<TResponse>(cached)!;
        }

        // --- Step 3: CACHE MISS — run the handler ---
        var result = await next();

        // --- Step 3b: Only cache SUCCESSFUL results ---
        // A business FAILURE (e.g. TransferResult.Success=false "Insufficient funds")
        // must NOT be cached for 24h: the cause is transient/correctable (the client
        // tops up the account, then retries with the same key) and replaying the stale
        // "insufficient funds" for a full day would be wrong. We inspect a conventional
        // `bool Success` property if the response exposes one; absent that property we
        // treat the result as cacheable (queries and void-ish results have no failure
        // semantics). Chosen over a short-TTL-for-failures scheme because "don't cache
        // failures at all" is the cleaner contract — a retry simply re-runs the handler.
        if (!IsSuccessful(result))
        {
            _logger.LogInformation(
                "Idempotency key {IdempotencyKey} NOT cached — handler returned a " +
                "business failure; a retry will re-execute (failures are not replayed)",
                headerKey);
            return result;
        }

        // --- Step 4: Store the result against the key with a 24h TTL ---
        // Eventual-consistency trade-off (unchanged from the DB design): if we crash
        // between the handler's commit and this Set, the key is not stored, so a retry
        // re-executes. Exactly-once would need an outbox; idempotency-key-with-TTL is the
        // standard Stripe-style contract. Concurrent duplicates that both miss will both
        // run the handler (last-writer-wins on the Set) — the durable transfer write is
        // still atomic at the EF level; this store only short-circuits FUTURE retries.
        // A Redis outage here must not fail an otherwise-successful transfer, so the Set
        // is best-effort: log and continue rather than surface a 500 to the client.
        try
        {
            await _cache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(result),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                cancellationToken);

            _logger.LogInformation(
                "Idempotency key {IdempotencyKey} stored — response cached for 24h",
                headerKey);
        }
        catch (Exception ex)
        {
            // Cache write failed (e.g. Redis unreachable). The handler's mutation already
            // committed; we just couldn't persist the dedupe key. Degrade, don't fail.
            _logger.LogWarning(ex,
                "Idempotency key {IdempotencyKey} could not be cached — " +
                "a retry with this key may re-execute until the cache recovers",
                headerKey);
        }

        return result;
    }

    // Convention-based success probe: if TResponse exposes a public readable `bool Success`
    // property (TransferResult, CreateAccountResult both do), use it. Anything without that
    // property (queries, primitives) has no business-failure concept, so it is cacheable.
    // Reflection cost is one cached PropertyInfo lookup per closed generic type; the JIT
    // closes TResponse once, so this is effectively a static per-type read.
    private static bool IsSuccessful(TResponse result)
    {
        if (result is null)
        {
            return false; // a null response is not a success worth replaying
        }

        var successProp = SuccessProperty;
        if (successProp is null)
        {
            return true; // no Success property → no failure semantics → cacheable
        }

        return successProp.GetValue(result) is not bool ok || ok;
    }

    private static readonly System.Reflection.PropertyInfo? SuccessProperty =
        typeof(TResponse).GetProperty(
            "Success",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            is { CanRead: true, PropertyType: var t } p && t == typeof(bool)
            ? p
            : null;
}
