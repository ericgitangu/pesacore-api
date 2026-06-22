namespace PesaCore.Models;

// Entity class — maps 1:1 to a database table via EF Core conventions.
// EF sees "Account" → looks for/creates table named "Accounts" (pluralized).
// Java equivalent: @Entity public class Account { ... }
// Python equivalent: class Account(Base) in SQLAlchemy
public class Account
{
    // EF convention: property named "Id" or "AccountId" becomes the primary key automatically.
    // No annotation needed — Java would require @Id, Python/SQLAlchemy needs primary_key=True.
    public int Id { get; set; }

    // "required" (C# 11) — compile-time enforcement that this must be set on construction.
    // Different from nullable reference types (!): required is about initialization, not nullability.
    // Without required, you could construct new Account { } and AccountNumber would be null,
    // which would only fail at runtime when EF tries to INSERT a non-nullable column.
    // Java equivalent: no direct equivalent — you'd rely on @NotNull + runtime validation.
    public required string AccountNumber { get; set; }
    public required string HolderName { get; set; }

    // decimal, not double — critical for money.
    // double has floating-point precision errors: 0.1 + 0.2 == 0.30000000000000004
    // decimal is 128-bit with exact base-10 representation: 0.1 + 0.2 == 0.3
    // Banking rule: always decimal for currency. CBK reporting requires exact figures.
    // Java equivalent: BigDecimal. Python equivalent: decimal.Decimal.
    public decimal Balance { get; set; }

    // Navigation property — EF Core's way of representing the "many" side of one-to-many.
    // EF uses this to know: Account has many Transactions.
    // [] is C# 12 collection expression — shorthand for new List<Transaction>().
    // Initializing to empty list avoids null checks when no transactions are loaded.
    // Java/JPA equivalent: @OneToMany(mappedBy = "account") List<Transaction> transactions;
    public List<Transaction> Transactions { get; set; } = [];
}

public class Transaction
{
    public int Id { get; set; }

    // Foreign key — EF convention: property named "{NavigationProperty}Id" is auto-detected
    // as the FK for the Account navigation property below. No [ForeignKey] annotation needed.
    // Java/JPA equivalent: @ManyToOne @JoinColumn(name = "account_id")
    public int AccountId { get; set; }

    // The "m" suffix on decimal literals (100m, 10000m) marks them as decimal, not double.
    // Without it: 100 is int, 100.0 is double, 100m is decimal.
    public decimal Amount { get; set; }
    public DateTime Timestamp { get; set; }
    public required string Description { get; set; }

    // Inverse navigation — the "one" side of many-to-one.
    // "= null!" means: "I know this looks null, but I promise EF will populate it."
    // The "!" is the null-forgiving operator — suppresses the nullable reference type warning.
    // At runtime null! is just null — it's purely a compiler hint, no runtime effect.
    // EF populates this via Include() (eager) or lazy loading proxies.
    // If you access Account without loading it, you get null — not an exception.
    public Account Account { get; set; } = null!;
}
