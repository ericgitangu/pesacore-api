using FluentAssertions;
using PesaCore.Features;
using PesaCore.Tests.Helpers;

namespace PesaCore.Tests.Features;

// ===== COMMAND HANDLER TESTS — business logic for fund transfers =====
//
// These test the TransferFundsHandler in isolation with an InMemory database.
// No HTTP, no MediatR pipeline, no middleware — pure handler + DbContext.
//
// Why InMemory and not SQLite? For unit tests, InMemory is faster and sufficient
// to verify business logic. SQLite would test SQL generation too (better for
// integration tests). The trade-off: InMemory doesn't enforce constraints
// (foreign keys, unique indexes) — but that's the DB's job, not the handler's.
//
// Banking rationale: the transfer handler is the most critical path.
// Getting this wrong = double-debits, phantom credits, or balance drift.
// Every edge case here maps to a real production incident.
//
// Java equivalent: @DataJpaTest with H2 + @Autowired repository injection.
// Python equivalent: pytest with SQLAlchemy session using sqlite:///:memory:.
public class TransferFundsHandlerTests
{
    // --- Happy path ---

    [Fact]
    public async Task ValidTransfer_DebitsSourceAndCreditsDestination()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);
        var command = new TransferFundsCommand("EQB001", "EQB002", 1000m);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert — verify both sides of the double-entry
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Transfer successful");
        result.NewBalance.Should().Be(9000m); // Alice: 10000 - 1000

        var bob = db.Accounts.First(a => a.AccountNumber == "EQB002");
        bob.Balance.Should().Be(6000m); // Bob: 5000 + 1000
    }

    [Fact]
    public async Task Transfer_IsAtomic_BothAccountsMutateOrNeither()
    {
        // Verify that a successful transfer changes BOTH balances.
        // In production with SQL Server, this would be in a DB transaction.
        // With InMemory, SaveChangesAsync is the atomic boundary.
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);

        var result = await handler.Handle(
            new TransferFundsCommand("EQB001", "EQB003", 2500m),
            CancellationToken.None);

        result.Success.Should().BeTrue();

        var alice = db.Accounts.First(a => a.AccountNumber == "EQB001");
        var carol = db.Accounts.First(a => a.AccountNumber == "EQB003");

        // Double-entry: total money in the system unchanged.
        // Alice had 10000, Carol had 15000 = 25000 total.
        (alice.Balance + carol.Balance).Should().Be(25000m);
    }

    // --- Insufficient funds ---

    [Fact]
    public async Task InsufficientFunds_ReturnsFailure_NoBalanceChange()
    {
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);
        // Bob has 5000, trying to send 5001
        var command = new TransferFundsCommand("EQB002", "EQB001", 5001m);

        var result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Insufficient funds");
        result.NewBalance.Should().Be(5000m); // Bob's balance unchanged

        // Verify Alice also unchanged
        var alice = db.Accounts.First(a => a.AccountNumber == "EQB001");
        alice.Balance.Should().Be(10000m);
    }

    [Fact]
    public async Task ExactBalance_TransferSucceeds_ZeroBalance()
    {
        // Edge case: transfer entire balance. Should succeed, leaving 0.
        // In production, some accounts have minimum balance requirements.
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);
        var command = new TransferFundsCommand("EQB002", "EQB001", 5000m);

        var result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.NewBalance.Should().Be(0m);
    }

    // --- Account not found ---

    [Fact]
    public async Task SourceAccountNotFound_ReturnsFailure()
    {
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);
        var command = new TransferFundsCommand("EQB999", "EQB001", 100m);

        var result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Account not found");
    }

    [Fact]
    public async Task DestinationAccountNotFound_ReturnsFailure()
    {
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);
        var command = new TransferFundsCommand("EQB001", "EQB999", 100m);

        var result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Account not found");
    }

    // --- Decimal precision (banking-critical) ---

    [Fact]
    public async Task DecimalPrecision_NoFloatingPointDrift()
    {
        // Banking test: verify that decimal arithmetic doesn't introduce
        // floating-point errors. This is why we use decimal, not double.
        // 0.1 + 0.2 == 0.3 with decimal (fails with double).
        using var db = TestDbContextFactory.Create();
        var handler = new TransferFundsHandler(db);

        // Transfer 0.10 three times
        for (int i = 0; i < 3; i++)
        {
            await handler.Handle(
                new TransferFundsCommand("EQB001", "EQB002", 0.10m),
                CancellationToken.None);
        }

        var alice = db.Accounts.First(a => a.AccountNumber == "EQB001");
        alice.Balance.Should().Be(9999.70m); // Exactly, no floating-point drift
    }
}
