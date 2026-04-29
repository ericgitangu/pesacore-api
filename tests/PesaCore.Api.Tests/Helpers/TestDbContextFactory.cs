using Microsoft.EntityFrameworkCore;
using PesaCore.Api.Data;
using PesaCore.Api.Models;

namespace PesaCore.Api.Tests.Helpers;

// Shared factory for creating in-memory BankDbContext instances for tests.
// Each test gets a unique database name (Guid) to ensure test isolation —
// InMemoryDatabase shares state across DbContext instances with the same name.
// Java equivalent: H2 in-memory database with unique JDBC URL per test.
// Python equivalent: SQLAlchemy create_engine("sqlite:///:memory:") per test.
public static class TestDbContextFactory
{
    public static BankDbContext Create()
    {
        var options = new DbContextOptionsBuilder<BankDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var context = new BankDbContext(options);

        // Seed the same 3 accounts as BankDbContext.OnModelCreating.
        // InMemoryDatabase does NOT run HasData() seeds — you must seed manually.
        // This is a known gotcha: InMemory skips OnModelCreating seed data.
        context.Accounts.AddRange(
            new Account { Id = 1, AccountNumber = "EQB001", HolderName = "Alice", Balance = 10000m },
            new Account { Id = 2, AccountNumber = "EQB002", HolderName = "Bob", Balance = 5000m },
            new Account { Id = 3, AccountNumber = "EQB003", HolderName = "Carol", Balance = 15000m }
        );
        context.SaveChanges();

        return context;
    }
}
