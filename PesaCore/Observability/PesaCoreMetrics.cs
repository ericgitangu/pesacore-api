using System.Diagnostics.Metrics;

namespace PesaCore.Observability;

// ===== CUSTOM BUSINESS METRICS — System.Diagnostics.Metrics =====
//
// This is the native .NET metrics API (not prometheus-net, not a vendor SDK).
// A Meter is the factory for instruments; OpenTelemetry's MeterProvider picks it
// up by name via .AddMeter("PesaCore.Metrics") in Program.cs.
//
// Why a singleton wrapper instead of static fields?
//   - The handler/behavior take IPesaCoreMetrics via DI — testable (can mock/no-op),
//     and the Meter is disposed with the container instead of leaking process-wide.
//   - The Meter name "PesaCore.Metrics" is the contract the OTel pipeline subscribes to.
//
// Instrument naming follows OTel semantic conventions (dotted, lowercase). When
// exported to Prometheus the collector/exporter rewrites dots -> underscores and
// appends unit/`_total` suffixes, e.g.:
//   pesacore.transfers.total          -> pesacore_transfers_total
//   pesacore.transfer.amount (KES)    -> pesacore_transfer_amount_KES_*  (histogram buckets)
//   pesacore.idempotency.hits         -> pesacore_idempotency_hits_total
//
// Java equivalent: Micrometer MeterRegistry + Counter/DistributionSummary.
public interface IPesaCoreMetrics
{
    // outcome tag is one of: "success", "insufficient_funds", "not_found".
    void RecordTransfer(string outcome, decimal? amount);
    void RecordIdempotencyHit();

    // Account lifecycle — incremented when CreateAccountHandler opens a new account.
    void RecordAccountCreated();

    // Cache-aside instrumentation (ADR 0002). `region` tags WHICH read populated
    // the cache (e.g. "account_balance") so the hit-ratio panel can be sliced per
    // read path. Hit = served from IDistributedCache; miss = had to hit Postgres.
    void RecordCacheHit(string region);
    void RecordCacheMiss(string region);
}

// No-op implementation. Used as the default when a handler/behavior is constructed
// outside the DI container (e.g. unit tests that `new` the type directly) so those
// call sites need not thread a Meter through. In production the DI-registered
// PesaCoreMetrics is injected and the real instruments fire.
public sealed class NoopPesaCoreMetrics : IPesaCoreMetrics
{
    public static readonly NoopPesaCoreMetrics Instance = new();
    public void RecordTransfer(string outcome, decimal? amount) { }
    public void RecordIdempotencyHit() { }
    public void RecordAccountCreated() { }
    public void RecordCacheHit(string region) { }
    public void RecordCacheMiss(string region) { }
}

public sealed class PesaCoreMetrics : IPesaCoreMetrics, IDisposable
{
    public const string MeterName = "PesaCore.Metrics";

    private readonly Meter _meter;
    private readonly Counter<long> _transfersTotal;
    private readonly Histogram<double> _transferAmount;
    private readonly Counter<long> _idempotencyHits;
    private readonly Counter<long> _accountsCreated;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;

    public PesaCoreMetrics(IMeterFactory meterFactory)
    {
        // IMeterFactory (added in .NET 8) is the DI-friendly way to create Meters —
        // it scopes the Meter lifetime to the container and tags it for the OTel pipeline.
        _meter = meterFactory.Create(MeterName);

        _transfersTotal = _meter.CreateCounter<long>(
            name: "pesacore.transfers.total",
            unit: "{transfer}",
            description: "Total fund-transfer attempts, tagged by outcome.");

        _transferAmount = _meter.CreateHistogram<double>(
            name: "pesacore.transfer.amount",
            unit: "KES",
            description: "Distribution of transfer amounts for successful transfers.");

        _idempotencyHits = _meter.CreateCounter<long>(
            name: "pesacore.idempotency.hits",
            unit: "{hit}",
            description: "Idempotency cache hits — duplicate submissions short-circuited.");

        _accountsCreated = _meter.CreateCounter<long>(
            name: "pesacore.accounts.created",
            unit: "{account}",
            description: "Total accounts opened via CreateAccountHandler.");

        // Cache-aside counters (ADR 0002). Exported to Prometheus as
        //   pesacore.cache.hits   -> pesacore_cache_hits_total
        //   pesacore.cache.misses -> pesacore_cache_misses_total
        // hit-ratio = hits / (hits + misses); the "Cache" Grafana dashboard plots it.
        _cacheHits = _meter.CreateCounter<long>(
            name: "pesacore.cache.hits",
            unit: "{hit}",
            description: "Cache-aside hits — reads served from IDistributedCache (Upstash/Redis), no DB round-trip.");

        _cacheMisses = _meter.CreateCounter<long>(
            name: "pesacore.cache.misses",
            unit: "{miss}",
            description: "Cache-aside misses — reads that fell through to Postgres and re-populated the cache.");
    }

    public void RecordTransfer(string outcome, decimal? amount)
    {
        _transfersTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        // Only record amount for completed transfers — failures have no meaningful
        // "amount moved", and mixing them skews the distribution.
        if (outcome == "success" && amount is { } a)
        {
            _transferAmount.Record((double)a);
        }
    }

    public void RecordIdempotencyHit() => _idempotencyHits.Add(1);

    public void RecordAccountCreated() => _accountsCreated.Add(1);

    public void RecordCacheHit(string region) =>
        _cacheHits.Add(1, new KeyValuePair<string, object?>("region", region));

    public void RecordCacheMiss(string region) =>
        _cacheMisses.Add(1, new KeyValuePair<string, object?>("region", region));

    public void Dispose() => _meter.Dispose();
}
