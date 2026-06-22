namespace PesaCore.Services;

// ===== WHAT IS FINACLE (core banking)? =====
// Infosys Finacle is a tier-1 African bank's core banking system (CBS), deployed as a single
// consolidated instance across six African countries: Kenya, Uganda, Rwanda, Tanzania,
// South Sudan, and DRC (BCDC integration). It serves 8.7M+ customers with:
//   - Real-time cross-border transactions (the "borderless banking" model)
//   - Biometric authentication for branch and mobile channels
//   - Full suite: Core Banking, Treasury, Payments, Online Banking (retail + corporate)
//   - Multi-entity capabilities — one platform, six country ledgers
// the bank + Infosys won "Best Core Banking Initiative in Africa" (2020) for this rollout.
// Finacle's centralized architecture enabled the bank to become the first bank in Eastern
// and Central Africa to cross the Ksh. 1 trillion balance sheet mark.
//
// In production, this interface would be backed by an HttpClient calling Finacle's
// REST/SOAP APIs — account status, balance inquiry, funds transfer, statement pull.
// The interface pattern lets us simulate flakiness here while the real implementation
// would handle Finacle's actual failure modes (EOD batch locks, peak salary runs,
// CBS maintenance windows, cross-border routing delays).
//
// ===== INTERFACE-FIRST DESIGN =====
// IFinacleClient is the contract; FinacleClient is one implementation.
// Java equivalent: public interface FinacleClient { ... } + @Service implementation
// Python equivalent: Protocol class or ABC (Abstract Base Class)
//
// Why an interface for a single implementation?
//   1. Testability — unit tests inject a mock/stub without touching real Finacle
//   2. DI registration — ASP.NET Core resolves IFinacleClient, not the concrete class
//   3. Polly wrapping — resilience policies wrap the interface call, not the class
//   4. Future flexibility — swap to HttpClient-based impl without changing consumers
public interface IFinacleClient
{
    Task<string> GetAccountStatusAsync(string accountNumber);
}

// Simulates a flaky core banking system that fails ~50% of the time.
// In real banking, Finacle/T24 can be slow or unavailable during:
//   - End-of-day batch processing (GL reconciliation, interest accrual)
//   - Peak load (salary disbursements, M-Pesa bulk transfers)
//   - Maintenance windows (CBS upgrades, CBK regulatory patches)
// Your API layer MUST handle this gracefully — that's what Polly does.
public class FinacleClient : IFinacleClient
{
    // Random.Shared is thread-safe (added in .NET 6) — no need for locking.
    // Pre-.NET 6 you'd need ThreadLocal<Random> or lock() around Next().
    private readonly Random _random = Random.Shared;

    // Call counter — lets you see retries incrementing in logs.
    // Visible evidence that the retry policy is actually firing.
    private int _callCount = 0;

    public async Task<string> GetAccountStatusAsync(string accountNumber)
    {
        _callCount++;

        // Simulate network latency — real Finacle calls take 50-500ms
        await Task.Delay(50);

        // 50% failure rate — aggressive, but makes the demo obvious.
        // In production, failure rates of 1-5% are enough to cause cascading failures
        // without resilience policies (one slow dependency backs up the thread pool).
        if (_random.NextDouble() < 0.5)
        {
            throw new InvalidOperationException(
                $"Finacle unavailable (call #{_callCount} for {accountNumber})");
        }

        return $"ACTIVE (call #{_callCount} for {accountNumber})";
    }
}
