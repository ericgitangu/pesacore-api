using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using PesaCore.Data;
using PesaCore.Observability;

namespace PesaCore.Features;

// ===== CQRS COMMAND — changes state, has side effects =====
//
// Commands are the write side of CQRS. They mutate state and return a result
// indicating success/failure. Key differences from queries:
//   - NOT safe to retry blindly (double-debit risk)
//   - Need validation BEFORE execution (FluentValidation in Step 8)
//   - Need idempotency in production (idempotency key to prevent duplicate transfers)
//   - May publish domain events (TransferCompleted, InsufficientFunds)
//
// This is a fund transfer — the canonical banking operation.
// Real production version would add:
//   - Transaction wrapping (BEGIN/COMMIT/ROLLBACK)
//   - Idempotency key check (prevent double-submit)
//   - Outbox pattern for event publishing (ensure event + state change are atomic)
//   - Audit trail (who, when, what, from where)
//   - Regulatory checks (AML/KYC screening, CBK daily transfer limits)

// The Command — what the caller wants to happen.
// [FromBody] in the controller deserializes JSON into this record.
// Record properties map to JSON camelCase by default (ASP.NET Core convention):
//   { "fromAccount": "EQB001", "toAccount": "EQB002", "amount": 500 }
public record TransferFundsCommand(
    string FromAccount,
    string ToAccount,
    decimal Amount) : IRequest<TransferResult>;

// The Result — success/failure signal + message + updated state.
// Returning a result (not void) is a deliberate choice:
//   - Caller can show the new balance without a second query
//   - Error messages are structured, not exception-based
//   - No ambiguity about what happened
public record TransferResult(bool Success, string Message, decimal? NewBalance);

public class TransferFundsHandler : IRequestHandler<TransferFundsCommand, TransferResult>
{
    private readonly BankDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly IPesaCoreMetrics _metrics;

    // cache is optional so unit tests can `new` the handler without a cache (defaults
    // to a no-op in-memory cache); metrics is optional for the same reason. The DI
    // container always supplies the real IDistributedCache + PesaCoreMetrics in prod.
    public TransferFundsHandler(
        BankDbContext db,
        IDistributedCache? cache = null,
        IPesaCoreMetrics? metrics = null)
    {
        _db = db;
        _cache = cache ?? new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(
                new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        _metrics = metrics ?? NoopPesaCoreMetrics.Instance;
    }

    public async Task<TransferResult> Handle(
        TransferFundsCommand request,
        CancellationToken cancellationToken)
    {
        // Wrap load → check → debit/credit → save in an explicit DB transaction so the
        // overdraft re-check and the two balance writes commit (or roll back) as one unit.
        // Read-check-write WITHOUT this is a classic lost-update / double-spend window: two
        // concurrent transfers can both read balance=100, both pass the >=amount check, and
        // both debit — overdrawing the account. The transaction serialises the critical
        // section here; we ALSO re-read inside it so the check sees committed state.
        //
        // NOTE: a transaction alone is not a full fix for lost updates under READ COMMITTED
        // (the default for SQLite/Postgres) — the prod-grade fix is an optimistic-concurrency
        // token: a [Timestamp]/RowVersion column (xmin on Npgsql) so a concurrent writer's
        // SaveChanges throws DbUpdateConcurrencyException and is retried. That is the correct
        // production design; the transaction is the minimum correctness floor.
        //
        // The InMemory provider used by unit tests is NON-relational and throws on
        // BeginTransactionAsync, so we only open a transaction when the provider is
        // relational (SQLite in integration tests, Npgsql in prod). On InMemory the same
        // load/check/save runs directly — single-threaded test execution has no race to guard.
        var useTransaction = _db.Database.IsRelational();
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = useTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using var _ = tx;

        // Load both accounts — FirstOrDefaultAsync returns null if not found.
        var from = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.FromAccount, cancellationToken);
        var to = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.ToAccount, cancellationToken);

        if (from == null || to == null)
        {
            _metrics.RecordTransfer("not_found", null);
            return new TransferResult(false, "Account not found", null);
        }

        // Business rule: can't overdraft. Re-checked INSIDE the transaction against the
        // freshly-loaded balance. Real banking has more complex rules — overdraft limits,
        // hold amounts, pending transactions reducing available balance.
        if (from.Balance < request.Amount)
        {
            _metrics.RecordTransfer("insufficient_funds", null);
            return new TransferResult(false, "Insufficient funds", from.Balance);
        }

        // Mutation — EF Core's change tracker detects these property changes.
        // SaveChangesAsync generates UPDATE statements for both accounts.
        from.Balance -= request.Amount;
        to.Balance += request.Amount;

        // SaveChangesAsync: flushes all tracked changes to the database in one batch.
        // If this throws, neither account is modified (atomic at the EF level).
        // CancellationToken ensures we don't commit if the client has disconnected.
        await _db.SaveChangesAsync(cancellationToken);

        if (tx is not null)
        {
            await tx.CommitAsync(cancellationToken);
        }

        // --- Cache invalidation ---
        // Both balances just changed, so the cache-aside entries for both accounts are
        // now stale. Drop them; the next GetAccountBalance read repopulates from the DB.
        // Same key contract as the reader (AccountCacheKeys) so the keys cannot drift.
        // Best-effort: a cache outage must not fail a committed transfer — the 30s TTL
        // on the read side bounds staleness even if this removal is lost.
        try
        {
            await _cache.RemoveAsync(AccountCacheKeys.Balance(from.AccountNumber), cancellationToken);
            await _cache.RemoveAsync(AccountCacheKeys.Balance(to.AccountNumber), cancellationToken);
        }
        catch
        {
            // Invalidation failed — stale reads self-heal within the read-side TTL.
        }

        _metrics.RecordTransfer("success", request.Amount);
        return new TransferResult(true, "Transfer successful", from.Balance);
    }
}
