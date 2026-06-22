namespace PesaCore.Models;

// ===== IDEMPOTENCY RECORD — prevents double-submit in banking =====
//
// Problem: Client sends POST /cqrs/transfer. Server processes it (debits 500).
// Response gets lost in transit. Client retries. Server debits ANOTHER 500.
// Customer lost 1000 instead of 500. That's a regulatory incident.
//
// Solution: Client sends a unique key (UUID) in the X-Idempotency-Key header.
// Server checks: "Have I already processed this key?"
//   - Yes → return the cached response (no re-execution, no double-debit)
//   - No  → process normally, cache the response against the key
//
// This entity stores processed keys + their cached responses in the database.
// In production: Redis or a dedicated idempotency table with TTL-based cleanup.
// SQLite is fine for learning — the pattern is what matters.
//
// Java equivalent: @Entity IdempotencyRecord with Spring JPA
// Python equivalent: SQLAlchemy model or Redis key with TTL
public class IdempotencyRecord
{
    public int Id { get; set; }

    // The UUID from the X-Idempotency-Key header — must be unique.
    // Client generates this (typically a UUID v4) and sends it with every mutation.
    // If the client retries the same request, it sends the SAME key.
    public required string Key { get; set; }

    // Cached JSON response — returned verbatim on duplicate requests.
    // Includes both success and failure responses — if the original transfer
    // failed validation, the retry should return the same 400, not re-execute.
    public required string CachedResponse { get; set; }

    // HTTP status code of the original response (200, 400, etc.).
    // Needed to reconstruct the full HTTP response on cache hits.
    public int StatusCode { get; set; }

    // Audit timestamps — banking requires knowing when operations occurred.
    // ExpiresAt enables TTL-based cleanup: a background job or SQL job
    // deletes records where ExpiresAt < UTC_NOW. Keys don't live forever —
    // 24-48 hours is typical for payment idempotency.
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
