using Microsoft.EntityFrameworkCore;
using PesaCore.Api.Models;

namespace PesaCore.Api.Data;

// DbContext is EF Core's central object — it combines three patterns:
//   1. Unit of Work — tracks all changes made during a request, commits them in one SaveChangesAsync()
//   2. Identity Map — ensures only one in-memory object per database row (no duplicates)
//   3. Change Tracker — detects which properties changed and generates minimal UPDATE SQL
// Java equivalent: EntityManager in JPA/Hibernate (similar three responsibilities).
// Python equivalent: Session in SQLAlchemy.
//
// Lifetime: registered as Scoped in DI → one instance per HTTP request.
// Why Scoped? Each request should see its own change tracker and transaction boundary.
// Singleton would share state across requests (thread-safety nightmare).
// Transient would mean multiple instances per request can't share tracked entities.
public class BankDbContext : DbContext
{
    // Constructor takes DbContextOptions — this is how DI passes configuration
    // (connection string, provider, logging settings) from Program.cs into the context.
    // The ": base(options)" forwards to DbContext's constructor which stores the config.
    // Java equivalent: @PersistenceContext EntityManager em; (container-managed injection)
    public BankDbContext(DbContextOptions<BankDbContext> options) : base(options) { }

    // DbSet<T> — represents a queryable collection of entities mapped to a table.
    // _db.Accounts is the entry point for all Account queries (LINQ → SQL translation).
    // "=> Set<Account>()" uses the expression-bodied property syntax to call the base method.
    // Why Set<T>() instead of a field? DbContext.Set<T>() returns the same cached instance
    // each time — the property is just a convenient accessor, not creating a new DbSet.
    // Java equivalent: em.createQuery("FROM Account", Account.class)
    // Python equivalent: session.query(Account)
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();

    // Idempotency store — tracks processed mutation keys to prevent double-submit.
    // Lives in the same DB as domain data so key storage participates in the same
    // transaction as the handler's SaveChangesAsync (atomic commit).
    // Production would add a TTL cleanup job: DELETE WHERE ExpiresAt < UTC_NOW.
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    // OnModelCreating — called once at startup when EF builds the internal model.
    // This is where you configure relationships, indexes, constraints, and seed data.
    // HasData() is EF Core's seeding mechanism — runs on EnsureCreated() or migrations.
    // In production banking, seed data would be reference data (currency codes, branch IDs),
    // not test accounts. Customer data comes from migrations or ETL pipelines.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Unique index on idempotency key — the database enforces uniqueness,
        // catching race conditions where two concurrent requests with the same key
        // both pass the FirstOrDefaultAsync check before either inserts.
        // The second INSERT hits a unique constraint violation → no double-submit.
        // This is the last line of defense after the application-level check.
        modelBuilder.Entity<IdempotencyRecord>()
            .HasIndex(r => r.Key)
            .IsUnique();


        // Seed 3 accounts — HasData requires explicit Id values because EF needs to
        // track seed data across migrations (it uses the Id to detect adds/updates/deletes).
        // The "m" suffix (10000m) makes these decimal literals — required for the decimal property.
        modelBuilder.Entity<Account>().HasData(
            new Account { Id = 1, AccountNumber = "EQB001", HolderName = "Alice",
                          Balance = 10000m },
            new Account { Id = 2, AccountNumber = "EQB002", HolderName = "Bob",
                          Balance = 5000m },
            new Account { Id = 3, AccountNumber = "EQB003", HolderName = "Carol",
                          Balance = 15000m }
        );

        // Seed 5 transactions per account.
        // Note: HasData for entities with navigation properties must use the FK (AccountId),
        // NOT the navigation property (Account). EF seed data is "raw" — no change tracking.
        var transactions = new List<Transaction>();
        var txId = 1;
        for (int accountId = 1; accountId <= 3; accountId++)
        {
            for (int i = 0; i < 5; i++)
            {
                transactions.Add(new Transaction
                {
                    Id = txId++,
                    AccountId = accountId,
                    Amount = 100m * (i + 1),
                    Timestamp = DateTime.UtcNow.AddDays(-i),
                    Description = $"Transaction {i + 1} for account {accountId}"
                });
            }
        }
        modelBuilder.Entity<Transaction>().HasData(transactions);
    }
}
