using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PesaCore.Data;
using PesaCore.Features;
using PesaCore.Tests.Helpers;

namespace PesaCore.Tests.Features;

// ===== COMMAND HANDLER TESTS — opening accounts =====
//
// The write counterpart to GetAccountBalance, mirroring TransferFundsHandlerTests'
// style (InMemory DbContext, no HTTP, no pipeline). The DB starts EMPTY (no seed,
// ), so these assert the real allocation behavior: first account is EQB001,
// the next derives from the current max, and the row actually persists.
public class CreateAccountHandlerTests
{
    [Fact]
    public async Task FirstAccountOnEmptyLedger_IsEqb001_AndPersists()
    {
        using var db = TestDbContextFactory.CreateEmpty();
        var handler = new CreateAccountHandler(db);

        var result = await handler.Handle(
            new CreateAccountCommand("Dana", 2_500m), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.AccountNumber.Should().Be("EQB001");
        result.HolderName.Should().Be("Dana");
        result.Balance.Should().Be(2_500m);

        // Persisted, queryable by the allocated number.
        var persisted = db.Accounts.Single(a => a.AccountNumber == "EQB001");
        persisted.HolderName.Should().Be("Dana");
        persisted.Balance.Should().Be(2_500m);
    }

    [Fact]
    public async Task SecondAccount_DerivesNextSequenceFromMax()
    {
        using var db = TestDbContextFactory.CreateEmpty();
        var handler = new CreateAccountHandler(db);

        var first = await handler.Handle(
            new CreateAccountCommand("Dana", 0m), CancellationToken.None);
        var second = await handler.Handle(
            new CreateAccountCommand("Evan", 100m), CancellationToken.None);

        first.AccountNumber.Should().Be("EQB001");
        second.AccountNumber.Should().Be("EQB002");
        db.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public async Task NextSequence_ContinuesAboveExistingMax()
    {
        // Pre-seeded fixture holds EQB001..EQB003 — the next allocation must be EQB004.
        using var db = TestDbContextFactory.Create();
        var handler = new CreateAccountHandler(db);

        var result = await handler.Handle(
            new CreateAccountCommand("Dana", 500m), CancellationToken.None);

        result.AccountNumber.Should().Be("EQB004");
    }

    [Fact]
    public async Task ZeroOpeningBalance_IsAllowed()
    {
        using var db = TestDbContextFactory.CreateEmpty();
        var handler = new CreateAccountHandler(db);

        var result = await handler.Handle(
            new CreateAccountCommand("Dana", 0m), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Balance.Should().Be(0m);
    }

    [Fact]
    public async Task HolderName_IsTrimmed()
    {
        using var db = TestDbContextFactory.CreateEmpty();
        var handler = new CreateAccountHandler(db);

        var result = await handler.Handle(
            new CreateAccountCommand("  Dana  ", 10m), CancellationToken.None);

        result.HolderName.Should().Be("Dana");
        db.Accounts.Single().HolderName.Should().Be("Dana");
    }

    // --- Finding #4: concurrent creates that collide on the same EQBnnn don't 500 ---
    // The InMemory provider does NOT enforce the UNIQUE(AccountNumber) index, so this test
    // uses a real SQLite DB (shared :memory: connection) where the constraint is live. Two
    // handlers can read the same max sequence and compute the SAME number; the loser's
    // INSERT throws DbUpdateException. The handler's bounded retry must absorb that, NOT
    // surface an unhandled 500 — every create eventually gets a unique number.
    [Fact]
    public async Task ConcurrentCreates_OnSqlite_AllSucceed_NoDuplicateNumbers_NoUnhandledError()
    {
        // Held-open :memory: connection so the schema (and unique index) persists across
        // the multiple DbContexts the concurrent handlers each open.
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<BankDbContext>()
            .UseSqlite(conn)
            .Options;

        using (var schema = new BankDbContext(options))
        {
            schema.Database.EnsureCreated();
        }

        const int concurrency = 8;

        // Each task gets its OWN DbContext (DbContext is not thread-safe) over the SAME
        // SQLite DB — the realistic shape of concurrent requests hitting one database.
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            await using var db = new BankDbContext(options);
            var handler = new CreateAccountHandler(db);
            return await handler.Handle(
                new CreateAccountCommand($"Holder{i}", 100m * i), CancellationToken.None);
        }).ToArray();

        // No task should throw (the retry loop must absorb the unique-constraint losers).
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.Success, "every concurrent create must succeed");
        results.Select(r => r.AccountNumber)
            .Should().OnlyHaveUniqueItems("the unique index + retry must yield distinct numbers");

        await using var verify = new BankDbContext(options);
        (await verify.Accounts.CountAsync()).Should().Be(concurrency);
    }
}
