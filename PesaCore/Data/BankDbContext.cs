using Microsoft.EntityFrameworkCore;
using PesaCore.Models;

namespace PesaCore.Data;

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
    // This is where you configure relationships, indexes, and constraints.
    //
    // No seed data (HasData) any more. The app now runs against REAL persisted data:
    // Neon Postgres in prod, SQLite locally (ADR 0002). The DB starts EMPTY and accounts
    // are opened through the CreateAccount command. In production banking, seed data
    // would only ever be reference data (currency codes, branch IDs), never customer
    // accounts — customer data comes from onboarding flows, migrations, or ETL.
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

        // Account numbers are externally meaningful identifiers — enforce uniqueness at
        // the DB so two opens can never collide on the same EQBnnn (the last line of
        // defence behind the handler's max-sequence generation; see CreateAccount.cs TODO).
        modelBuilder.Entity<Account>()
            .HasIndex(a => a.AccountNumber)
            .IsUnique();
    }
}
