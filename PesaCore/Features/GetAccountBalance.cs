using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using PesaCore.Data;
using PesaCore.Observability;

namespace PesaCore.Features;

// ===== CQRS QUERY — reads data, no side effects =====
//
// CQRS (Command Query Responsibility Segregation) splits reads and writes:
//   Query  = "give me data" (no mutation, safe to cache, safe to retry)
//   Command = "change something" (mutation, needs idempotency, needs validation)
//
// MediatR implements this via IRequest<T> (the message) + IRequestHandler<TRequest, TResult> (the handler).
// The controller sends the message via IMediator.Send() — it doesn't know which handler runs.
// This decoupling means:
//   1. Controllers stay thin — just HTTP-to-message translation
//   2. Handlers own business logic — testable without HTTP
//   3. Cross-cutting concerns (logging, validation, caching) plug in via MediatR pipeline behaviors
//   4. Evolution path — at scale, queries can read from a denormalized view or different DB
//      without changing the controller or the query contract
//
// Java equivalent: Axon Framework's @QueryHandler or manual CQRS with Spring ApplicationEventPublisher
// Python equivalent: no direct equivalent — you'd build this with a mediator pattern or use a library like pymediatr

// The Query — a record implementing IRequest<T>.
// T is the return type. The "?" means this query might return null (account not found).
// Records are ideal for messages — immutable, value equality, concise.
public record GetAccountBalanceQuery(string AccountNumber) : IRequest<AccountBalanceResult?>;

// The Result — what the handler returns. Also a record.
// This is NOT the same as the entity — it's a purpose-built response shape.
// The query handler decides what data to include; the entity has more.
public record AccountBalanceResult(string AccountNumber, string HolderName, decimal Balance);

// ===== CACHE-ASIDE KEY CONTRACT =====
// Single source of truth for the balance cache key so the READ path (this handler)
// and the WRITE path (TransferFundsHandler invalidation) cannot drift. If the prefix
// or shape lived in two files, a transfer could invalidate "balance:EQB001" while the
// reader cached "bal:EQB001" → stale balance after a transfer. One helper, no drift.
public static class AccountCacheKeys
{
    public const string Region = "account_balance";

    public static string Balance(string accountNumber) => $"balance:{accountNumber}";

    // The account-LIST key. Invalidated by CreateAccountHandler so a freshly opened
    // account shows up on the next dashboard read. A single const (not per-account)
    // because the list is a whole-collection projection, not a per-row entry.
    public const string AccountList = "accounts:list";
}

// The Handler — one handler per query. Implements IRequestHandler<TQuery, TResult>.
// MediatR discovers this via assembly scanning (RegisterServicesFromAssembly in Program.cs).
// Handlers default to Transient lifetime — a new instance per Send() call.
// This is fine because they're lightweight and stateless.
public class GetAccountBalanceHandler : IRequestHandler<GetAccountBalanceQuery, AccountBalanceResult?>
{
    // Short TTL: balances change on transfer. We invalidate the key on a
    // successful transfer (write-through-invalidate), but the TTL is the SAFETY NET —
    // it bounds staleness if an invalidation is ever missed (e.g. a different write path
    // that doesn't go through TransferFundsHandler). 30s keeps the demo's reads cheap
    // without risking a visibly stale balance.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly BankDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly IPesaCoreMetrics _metrics;

    // Constructor injection — same DI pattern as controllers.
    // DbContext is Scoped, handler is Transient — both live within the HTTP request scope.
    // IDistributedCache: Upstash/Redis in prod, in-memory fallback locally.
    // metrics is optional so unit tests can `new` the handler with just a db + cache;
    // the DI container always supplies the real PesaCoreMetrics in production.
    public GetAccountBalanceHandler(
        BankDbContext db,
        IDistributedCache cache,
        IPesaCoreMetrics? metrics = null)
    {
        _db = db;
        _cache = cache;
        _metrics = metrics ?? NoopPesaCoreMetrics.Instance;
    }

    public async Task<AccountBalanceResult?> Handle(
        GetAccountBalanceQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = AccountCacheKeys.Balance(request.AccountNumber);

        // --- Cache-aside: look-aside read ---
        // A cache outage must NOT take down a read path, so the cache get/set are
        // best-effort: on any cache error we fall through to Postgres (the source of
        // truth) and just skip caching. Correctness never depends on Redis being up.
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                _metrics.RecordCacheHit(AccountCacheKeys.Region);
                return JsonSerializer.Deserialize<AccountBalanceResult>(cached);
            }
        }
        catch
        {
            // Cache read failed — degrade to DB. Counted as a miss below.
        }

        _metrics.RecordCacheMiss(AccountCacheKeys.Region);

        // Projection query — only fetches the 3 fields we need, not the full entity.
        // FirstOrDefaultAsync returns null if no match — the "?" on the return type.
        // CancellationToken: propagated from the HTTP request. If the client disconnects,
        // the token fires and EF cancels the DB query. Without this, orphaned queries
        // consume DB connections for nothing. In banking at scale, this matters.
        var account = await _db.Accounts
            .Where(a => a.AccountNumber == request.AccountNumber)
            .Select(a => new AccountBalanceResult(a.AccountNumber, a.HolderName, a.Balance))
            .FirstOrDefaultAsync(cancellationToken);

        // Only cache positive lookups — caching "not found" risks masking a freshly
        // created account for up to the TTL. Negative caching is a deliberate opt-in,
        // not a default.
        if (account is not null)
        {
            try
            {
                await _cache.SetStringAsync(
                    cacheKey,
                    JsonSerializer.Serialize(account),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                    cancellationToken);
            }
            catch
            {
                // Cache write failed — the read still returns correctly from the DB.
            }
        }

        return account;
    }
}
