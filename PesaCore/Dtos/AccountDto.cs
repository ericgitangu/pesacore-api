namespace PesaCore.Dtos;

// DTO (Data Transfer Object) — a contract between API and consumer.
// DTOs exist to decouple the internal entity shape from the external API shape.
// Why not just return the Account entity directly?
//   1. Leaks internal structure — consumers couple to your DB schema
//   2. Over-fetching — entity may have fields consumers don't need (or shouldn't see)
//   3. Circular references — Account → Transactions → Account → infinite JSON loop
//   4. Security — internal fields (password hashes, audit columns) leak to the response
// In banking: DTOs are mandatory. You never expose raw entities — CBK audit controls
// require explicit data boundary contracts.
//
// "record" (C# 9) vs "class":
//   record: immutable by default, value equality, concise positional syntax
//   class:  mutable by default, reference equality, verbose property declarations
// DTOs are data snapshots crossing a boundary — immutability is the right default.
// Java equivalent: public record AccountDto(String accountNumber, ...) {} (Java 16+)
// Python equivalent: @dataclass(frozen=True) or a Pydantic BaseModel
public record AccountDto(
    string AccountNumber,
    string HolderName,
    decimal Balance,
    int TransactionCount);
