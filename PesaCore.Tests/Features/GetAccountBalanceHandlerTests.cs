using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PesaCore.Features;
using PesaCore.Tests.Helpers;

namespace PesaCore.Tests.Features;

// ===== QUERY HANDLER TESTS — read-only projections =====
//
// Queries are the easy side of CQRS — no mutations, no transactions, no race conditions.
// But they still need tests: projection accuracy, null handling, and cancellation.
//
// Java equivalent: @DataJpaTest with @Query method verification.
// Python equivalent: testing SQLAlchemy query functions with assertions on results.
public class GetAccountBalanceHandlerTests
{
    // Real in-memory IDistributedCache — the same fallback Program.cs registers when no
    // Redis is configured (ADR 0002). A fresh instance per call keeps tests isolated.
    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task ExistingAccount_ReturnsBalanceResult()
    {
        using var db = TestDbContextFactory.Create();
        var handler = new GetAccountBalanceHandler(db, NewCache());

        var result = await handler.Handle(
            new GetAccountBalanceQuery("EQB001"),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.AccountNumber.Should().Be("EQB001");
        result.HolderName.Should().Be("Alice");
        result.Balance.Should().Be(10000m);
    }

    [Fact]
    public async Task NonExistentAccount_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var handler = new GetAccountBalanceHandler(db, NewCache());

        var result = await handler.Handle(
            new GetAccountBalanceQuery("EQB999"),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Query_DoesNotMutateState()
    {
        // CQRS contract: queries must be side-effect-free.
        // Call the query twice — state should be identical.
        using var db = TestDbContextFactory.Create();
        var handler = new GetAccountBalanceHandler(db, NewCache());
        var query = new GetAccountBalanceQuery("EQB001");

        var result1 = await handler.Handle(query, CancellationToken.None);
        var result2 = await handler.Handle(query, CancellationToken.None);

        result1.Should().BeEquivalentTo(result2);
    }
}
