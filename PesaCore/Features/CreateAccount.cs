using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using PesaCore.Models;
using PesaCore.Data;
using PesaCore.Observability;

namespace PesaCore.Features;

// ===== CQRS COMMAND — opens a new account =====
//
// The write counterpart to GetAccountBalance. Like TransferFundsCommand it mutates
// state, so it runs through the SAME MediatR pipeline (IdempotencyBehavior +
// FluentValidation). A POST with an X-Idempotency-Key replayed after a lost response
// returns the cached CreateAccountResult instead of opening a second account — the
// exact double-submit protection a money mutation needs (here: no duplicate ledger row).
//
// Account-number generation is deliberately simple for the demo: derive the next
// sequence from the current max EQBnnn and zero-pad to 3 digits ("EQB" + 001..).
// Format stays compatible with TransferFundsValidator's ^EQB\d{3,}$ so a freshly
// created account can immediately be a transfer source/destination.
//
// Production banking would NOT generate the account number this way:
//   - the max-sequence read + insert is a race (two concurrent creates can pick the
//     same number) — fine on a single-writer demo, wrong at scale. Real systems use a
//     DB sequence / IDENTITY, a dedicated number-allocation service, or a UNIQUE
//     constraint + retry. Left as a TODO below; the validator format is the contract.
//   - product-type prefixes, check digits (mod-97), and KYC onboarding would apply.

// The Command — what the caller wants opened.
//   { "holderName": "Dana", "openingBalance": 2500 }
public record CreateAccountCommand(
    string HolderName,
    decimal OpeningBalance) : IRequest<CreateAccountResult>;

// The Result — success/failure + the created account's shape so the caller can render
// it without a second round-trip (same philosophy as TransferResult).
public record CreateAccountResult(
    bool Success,
    string Message,
    string? AccountNumber,
    string? HolderName,
    decimal? Balance);

public class CreateAccountHandler : IRequestHandler<CreateAccountCommand, CreateAccountResult>
{
    private const string AccountPrefix = "EQB";

    private readonly BankDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly IPesaCoreMetrics _metrics;

    // cache + metrics optional so unit tests can `new` the handler without a Redis/Meter;
    // the DI container always supplies the real IDistributedCache + PesaCoreMetrics in prod.
    public CreateAccountHandler(
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

    // Bounded retries for the generate+insert race. Two concurrent creates can read the
    // same max sequence and compute the SAME EQBnnn; the UNIQUE(AccountNumber) index then
    // turns the loser's INSERT into a DbUpdateException. We catch that, regenerate against
    // the now-larger max, and retry a few times. This is a backstop, NOT the prod-grade
    // fix — a DB SEQUENCE / IDENTITY (or a dedicated number-allocation service) removes the
    // race at the source; see GenerateAccountNumberAsync's TODO. The InMemory provider used
    // by unit tests does not enforce unique indexes, so the catch is exercised by the SQLite
    // integration path; the loop is a no-op cost on the happy path.
    private const int MaxCreateRetries = 5;

    public async Task<CreateAccountResult> Handle(
        CreateAccountCommand request,
        CancellationToken cancellationToken)
    {
        var holderName = request.HolderName.Trim();
        Account account;

        for (var attempt = 1; ; attempt++)
        {
            var accountNumber = await GenerateAccountNumberAsync(cancellationToken);

            account = new Account
            {
                AccountNumber = accountNumber,
                HolderName = holderName,
                Balance = request.OpeningBalance
            };

            _db.Accounts.Add(account);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                break; // committed — leave the retry loop
            }
            catch (DbUpdateException) when (attempt < MaxCreateRetries)
            {
                // Lost the account-number race (unique-constraint violation). Detach the
                // failed entity so the next attempt's change tracker is clean, then
                // regenerate against the now-higher max and retry.
                _db.Entry(account).State = EntityState.Detached;
            }
        }

        // --- Cache invalidation (ADR 0002, write-through-invalidate) ---
        // The account-LIST cache is now stale (a new row exists). Drop it so the next
        // dashboard read repopulates from the DB. Best-effort: a cache outage must not
        // fail a committed account open — the short read-side TTL bounds staleness.
        try
        {
            await _cache.RemoveAsync(AccountCacheKeys.AccountList, cancellationToken);
        }
        catch
        {
            // Invalidation failed — list reads self-heal within the read-side TTL.
        }

        _metrics.RecordAccountCreated();
        return new CreateAccountResult(
            true, "Account opened", account.AccountNumber, account.HolderName, account.Balance);
    }

    // Next sequence = (max existing EQBnnn) + 1, zero-padded to 3 digits.
    // DB starts EMPTY (seed dropped), so the first account is EQB001.
    // TODO (production): replace this read-then-insert with a DB sequence / IDENTITY or a
    // UNIQUE(AccountNumber) constraint + retry — the max-read is racy under concurrency.
    private async Task<string> GenerateAccountNumberAsync(CancellationToken cancellationToken)
    {
        // Pull only the suffix of well-formed EQBnnn numbers and take the max in memory.
        // Parsing in C# (not SQL) keeps this provider-agnostic across Npgsql/SQLite —
        // SUBSTRING/CAST semantics differ between the two; account counts are tiny here.
        var numbers = await _db.Accounts
            .Select(a => a.AccountNumber)
            .ToListAsync(cancellationToken);

        var max = 0;
        foreach (var n in numbers)
        {
            if (n.StartsWith(AccountPrefix, StringComparison.Ordinal)
                && int.TryParse(n.AsSpan(AccountPrefix.Length), out var seq)
                && seq > max)
            {
                max = seq;
            }
        }

        return $"{AccountPrefix}{(max + 1):D3}";
    }
}
