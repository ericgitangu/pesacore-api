using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PesaCore.Behaviors;
using PesaCore.Features;

namespace PesaCore.Tests.Behaviors;

// ===== IDEMPOTENCY BEHAVIOR TESTS — pipeline intercept verification =====
//
// The IdempotencyBehavior is an IPipelineBehavior that wraps every MediatR handler.
// The store moved from the DbContext to IDistributedCache (shared ephemeral
// state — correct under multi-instance scale-to-zero). These tests use a real
// MemoryDistributedCache (the same in-memory fallback Program.cs registers when no
// Redis is configured), not the DB, and assert the banking-critical contract:
//   1. GET/HEAD/OPTIONS → skip (queries are naturally idempotent)
//   2. POST without X-Idempotency-Key → 400 rejection
//   3. POST with key, cache miss → handler executes, result cached
//   4. POST with key, cache hit → handler does NOT execute, cached result returned
//
// Java equivalent: testing an Axon CommandHandler interceptor with mock CommandMessage.
// Python equivalent: testing a decorator with mock request context.
public class IdempotencyBehaviorTests
{
    private readonly Mock<ILogger<IdempotencyBehavior<TransferFundsCommand, TransferResult>>> _logger = new();

    // A real in-memory IDistributedCache — the same implementation Program.cs falls back
    // to when ConnectionStrings:Redis is unset. Exercising the real interface (not a mock)
    // means these tests verify the actual get/set serialization round-trip.
    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private const string KeyPrefix = "idem:";

    private static Mock<IHttpContextAccessor> CreateHttpAccessor(string method, string? idempotencyKey = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (idempotencyKey != null)
            httpContext.Request.Headers["X-Idempotency-Key"] = idempotencyKey;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);
        return accessor;
    }

    // Helper: MediatR 14 on net10.0, RequestHandlerDelegate<T> takes a CancellationToken
    // and returns Task<T>. We wrap our test logic in a lambda matching that signature.
    private static RequestHandlerDelegate<TransferResult> MakeNext(
        Func<TransferResult> resultFactory, Action? onCall = null)
    {
        return (CancellationToken _) =>
        {
            onCall?.Invoke();
            return Task.FromResult(resultFactory());
        };
    }

    // --- GET requests skip idempotency (naturally idempotent) ---

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ReadMethods_SkipIdempotencyCheck(string method)
    {
        var accessor = CreateHttpAccessor(method);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            NewCache(), accessor.Object, _logger.Object);

        var handlerCalled = false;
        var next = MakeNext(
            () => new TransferResult(true, "OK", 100m),
            () => handlerCalled = true);

        await behavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 100m),
            next,
            CancellationToken.None);

        handlerCalled.Should().BeTrue("read methods should pass through to handler");
    }

    // --- POST without key → rejection ---

    [Fact]
    public async Task PostWithoutKey_ThrowsMissingIdempotencyKeyException()
    {
        var accessor = CreateHttpAccessor("POST"); // no key
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            NewCache(), accessor.Object, _logger.Object);

        var next = MakeNext(() => new TransferResult(true, "OK", 100m));

        // Act & Assert — the behavior should reject before the handler runs.
        // In the global exception handler, this maps to HTTP 400.
        var act = () => behavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 100m),
            next,
            CancellationToken.None);

        await act.Should().ThrowAsync<MissingIdempotencyKeyException>();
    }

    // --- POST with key, cache miss → handler executes + result cached ---

    [Fact]
    public async Task PostWithKey_CacheMiss_ExecutesHandlerAndCachesResult()
    {
        var cache = NewCache();
        var key = Guid.NewGuid().ToString();
        var accessor = CreateHttpAccessor("POST", key);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            cache, accessor.Object, _logger.Object);

        var expectedResult = new TransferResult(true, "Transfer successful", 9000m);
        var handlerCallCount = 0;
        var next = MakeNext(
            () => expectedResult,
            () => handlerCallCount++);

        var result = await behavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 1000m),
            next,
            CancellationToken.None);

        // Handler ran exactly once
        handlerCallCount.Should().Be(1);
        result.Should().BeEquivalentTo(expectedResult);

        // Key is stored in the distributed cache under the TYPE-namespaced key (finding #1)
        var cached = await cache.GetStringAsync($"{KeyPrefix}{nameof(TransferFundsCommand)}:{key}");
        cached.Should().NotBeNull("the behavior caches the response against the idempotency key");
        var roundTripped = JsonSerializer.Deserialize<TransferResult>(cached!);
        roundTripped.Should().BeEquivalentTo(expectedResult);
    }

    // --- POST with key, cache hit → handler does NOT execute ---

    [Fact]
    public async Task PostWithKey_CacheHit_ReturnsCachedResult_HandlerSkipped()
    {
        var cache = NewCache();
        var key = Guid.NewGuid().ToString();
        var cachedResult = new TransferResult(true, "Transfer successful", 9000m);

        // Pre-populate the cache (simulating a prior request with this key, possibly
        // from another instance — the whole point of the distributed store).
        await cache.SetStringAsync(
            $"{KeyPrefix}{nameof(TransferFundsCommand)}:{key}", JsonSerializer.Serialize(cachedResult));

        var accessor = CreateHttpAccessor("POST", key);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            cache, accessor.Object, _logger.Object);

        var handlerCalled = false;
        var next = MakeNext(
            () => new TransferResult(true, "SHOULD NOT SEE THIS", 0m),
            () => handlerCalled = true);

        var result = await behavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 1000m),
            next,
            CancellationToken.None);

        // Handler was NOT called — double-submit prevented
        handlerCalled.Should().BeFalse("cached response should be returned without running handler");
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Transfer successful");
        result.NewBalance.Should().Be(9000m);
    }

    // --- Finding #1: cache key is scoped by request TYPE ---
    // The SAME X-Idempotency-Key used on two DIFFERENT command types must NOT collide.
    // Pre-type-scoping, `idem:{key}` was shared, so a /transfer then /accounts with the
    // same key would cross-deserialize one's payload into the other's TResponse. With
    // `idem:{TRequest.Name}:{key}` each operation has its own slot.
    [Fact]
    public async Task SameKey_DifferentRequestTypes_DoNotCollide()
    {
        var cache = NewCache();           // ONE shared store, as in production (Redis)
        var key = Guid.NewGuid().ToString(); // SAME key reused across both endpoints

        // 1) Run a transfer with the key — caches under idem:TransferFundsCommand:{key}
        var transferAccessor = CreateHttpAccessor("POST", key);
        var transferBehavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            cache, transferAccessor.Object, _logger.Object);
        var transferResult = new TransferResult(true, "Transfer successful", 9000m);
        var transfer = await transferBehavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 1000m),
            MakeNext(() => transferResult),
            CancellationToken.None);

        // 2) Run a create with the SAME key — must MISS (different type slot) and run its
        //    handler, NOT return the TransferResult cross-deserialized into CreateAccountResult.
        var createLogger = new Mock<ILogger<IdempotencyBehavior<CreateAccountCommand, CreateAccountResult>>>();
        var createAccessor = CreateHttpAccessor("POST", key);
        var createBehavior = new IdempotencyBehavior<CreateAccountCommand, CreateAccountResult>(
            cache, createAccessor.Object, createLogger.Object);
        var createHandlerRan = false;
        var createResult = new CreateAccountResult(true, "Account opened", "EQB007", "Dana", 500m);
        var create = await createBehavior.Handle(
            new CreateAccountCommand("Dana", 500m),
            (CancellationToken _) => { createHandlerRan = true; return Task.FromResult(createResult); },
            CancellationToken.None);

        transfer.Should().BeEquivalentTo(transferResult);
        createHandlerRan.Should().BeTrue("the create's key slot is distinct from the transfer's — it must miss and execute");
        create.Should().BeEquivalentTo(createResult);
        create.AccountNumber.Should().Be("EQB007", "the create must NOT inherit the transfer's cached payload");

        // Both slots coexist under distinct, type-scoped keys.
        (await cache.GetStringAsync($"{KeyPrefix}TransferFundsCommand:{key}")).Should().NotBeNull();
        (await cache.GetStringAsync($"{KeyPrefix}CreateAccountCommand:{key}")).Should().NotBeNull();
    }

    // --- Finding #2: business FAILURES are NOT cached (not replayed for 24h) ---
    // A failed result (Success=false, e.g. "Insufficient funds") must not be stored, so a
    // retry with the same key after the cause is fixed RE-EXECUTES the handler rather than
    // replaying the stale failure.
    [Fact]
    public async Task PostWithKey_BusinessFailure_IsNotCached_AndRetryReExecutes()
    {
        var cache = NewCache();
        var key = Guid.NewGuid().ToString();
        var accessor = CreateHttpAccessor("POST", key);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            cache, accessor.Object, _logger.Object);

        var callCount = 0;
        // First call fails (insufficient funds); a later retry succeeds (funds topped up).
        RequestHandlerDelegate<TransferResult> next = (CancellationToken _) =>
        {
            callCount++;
            return Task.FromResult(callCount == 1
                ? new TransferResult(false, "Insufficient funds", 0m)
                : new TransferResult(true, "Transfer successful", 9000m));
        };

        var first = await behavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 1000m), next, CancellationToken.None);

        first.Success.Should().BeFalse();
        // The failure must NOT be in the cache.
        (await cache.GetStringAsync($"{KeyPrefix}TransferFundsCommand:{key}"))
            .Should().BeNull("business failures must not be cached");

        // Retry with the SAME key re-executes (does not replay the stale failure).
        var second = await behavior.Handle(
            new TransferFundsCommand("EQB001", "EQB002", 1000m), next, CancellationToken.None);

        callCount.Should().Be(2, "the failed first result was not cached, so the retry re-ran the handler");
        second.Success.Should().BeTrue();
        // Now the SUCCESS is cached.
        (await cache.GetStringAsync($"{KeyPrefix}TransferFundsCommand:{key}"))
            .Should().NotBeNull("successful results are cached for replay");
    }
}
