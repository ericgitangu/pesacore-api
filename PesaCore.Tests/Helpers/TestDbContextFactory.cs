using Microsoft.EntityFrameworkCore;
using PesaCore.Data;
using PesaCore.Models;

namespace PesaCore.Tests.Helpers;

// Shared factory for creating in-memory BankDbContext instances for tests.
// Each test gets a unique database name (Guid) to ensure test isolation —
// InMemoryDatabase shares state across DbContext instances with the same name.
// Java equivalent: H2 in-memory database with unique JDBC URL per test.
// Python equivalent: SQLAlchemy create_engine("sqlite:///:memory:") per test.
public static class TestDbContextFactory
{
    // An EMPTY in-memory context — mirrors the real app's starting state (no seed,
    // ). Use this for create-then-assert tests (e.g. CreateAccountHandler,
    // where the first allocated number must be EQB001).
    public static BankDbContext CreateEmpty()
    {
        var options = new DbContextOptionsBuilder<BankDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new BankDbContext(options);
    }

    // A context pre-populated with three accounts as TEST ARRANGE (not app seed —
    // the app no longer seeds). This is create-then-assert at the data layer: the
    // accounts exist because the test put them there, exactly as a prior CreateAccount
    // command would have. Transfer/GetBalance handler unit tests use this fixture.
    // The account numbers follow the EQBnnn format the validator and generator use.
    public static BankDbContext Create()
    {
        var context = CreateEmpty();

        context.Accounts.AddRange(
            new Account { Id = 1, AccountNumber = "EQB001", HolderName = "Alice", Balance = 10000m },
            new Account { Id = 2, AccountNumber = "EQB002", HolderName = "Bob", Balance = 5000m },
            new Account { Id = 3, AccountNumber = "EQB003", HolderName = "Carol", Balance = 15000m }
        );
        context.SaveChanges();

        return context;
    }
}
