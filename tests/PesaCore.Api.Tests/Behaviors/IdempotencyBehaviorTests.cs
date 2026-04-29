using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using PesaCore.Api.Behaviors;
using PesaCore.Api.Features;
using PesaCore.Api.Tests.Helpers;

namespace PesaCore.Api.Tests.Behaviors;

// ===== IDEMPOTENCY BEHAVIOR TESTS — pipeline intercept verification =====
//
// The IdempotencyBehavior is an IPipelineBehavior that wraps every MediatR handler.
// Testing it requires mocking IHttpContextAccessor (to simulate HTTP headers) and
// RequestHandlerDelegate (to simulate the next step in the pipeline).
//
// These tests verify the banking-critical contract:
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
        using var db = TestDbContextFactory.Create();
        var accessor = CreateHttpAccessor(method);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            db, accessor.Object, _logger.Object);

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
        using var db = TestDbContextFactory.Create();
        var accessor = CreateHttpAccessor("POST"); // no key
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            db, accessor.Object, _logger.Object);

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
        using var db = TestDbContextFactory.Create();
        var key = Guid.NewGuid().ToString();
        var accessor = CreateHttpAccessor("POST", key);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            db, accessor.Object, _logger.Object);

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

        // Key is stored in the database
        var record = db.IdempotencyRecords.FirstOrDefault(r => r.Key == key);
        record.Should().NotBeNull();
        record!.StatusCode.Should().Be(200);
        record.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    // --- POST with key, cache hit → handler does NOT execute ---

    [Fact]
    public async Task PostWithKey_CacheHit_ReturnsCachedResult_HandlerSkipped()
    {
        using var db = TestDbContextFactory.Create();
        var key = Guid.NewGuid().ToString();
        var cachedResult = new TransferResult(true, "Transfer successful", 9000m);

        // Pre-populate the cache (simulating a prior request with this key)
        db.IdempotencyRecords.Add(new Models.IdempotencyRecord
        {
            Key = key,
            CachedResponse = JsonSerializer.Serialize(cachedResult),
            StatusCode = 200,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        db.SaveChanges();

        var accessor = CreateHttpAccessor("POST", key);
        var behavior = new IdempotencyBehavior<TransferFundsCommand, TransferResult>(
            db, accessor.Object, _logger.Object);

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
}
