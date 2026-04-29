using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PesaCore.Api.Middleware;

namespace PesaCore.Api.Tests.Middleware;

// ===== CORRELATION ID MIDDLEWARE TESTS — distributed tracing contract =====
//
// The CorrelationIdMiddleware has three responsibilities:
//   1. Extract X-Correlation-Id from incoming request (or generate new GUID)
//   2. Echo the ID back on the response header
//   3. Push the ID into Serilog's LogContext (tested implicitly via integration tests)
//
// These tests verify the HTTP header contract without Serilog — the middleware
// should work correctly even without the logging infrastructure.
//
// Banking rationale: correlation IDs are non-negotiable for incident reconstruction.
// If this middleware silently fails, debugging distributed payment failures becomes
// a multi-hour log-grepping exercise instead of a single search.
//
// Java equivalent: testing a Servlet Filter with MockHttpServletRequest/Response.
// Python equivalent: testing ASGI middleware with httpx.ASGITransport.
public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task NoIncomingHeader_GeneratesNewGuid_SetsOnResponse()
    {
        // Arrange — request with no correlation ID header
        var context = new DefaultHttpContext();
        var middleware = new CorrelationIdMiddleware(next: _ => Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — response should have a newly generated GUID
        var responseHeader = context.Response.Headers["X-Correlation-Id"].FirstOrDefault();
        responseHeader.Should().NotBeNullOrEmpty();
        Guid.TryParse(responseHeader, out _).Should().BeTrue("generated ID should be a valid GUID");
    }

    [Fact]
    public async Task IncomingHeader_PropagatesExistingId()
    {
        // Arrange — upstream service or client sent a correlation ID
        var existingId = "upstream-correlation-abc-123";
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = existingId;
        var middleware = new CorrelationIdMiddleware(next: _ => Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — same ID echoed back, not a new one
        var responseHeader = context.Response.Headers["X-Correlation-Id"].FirstOrDefault();
        responseHeader.Should().Be(existingId);
    }

    [Fact]
    public async Task Middleware_CallsNext_PipelineContinues()
    {
        // Verify the middleware doesn't short-circuit — it must call _next
        var context = new DefaultHttpContext();
        var nextCalled = false;
        var middleware = new CorrelationIdMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue("middleware must call next to continue the pipeline");
    }

    [Fact]
    public async Task EachRequest_GetsDifferentId()
    {
        // Verify uniqueness — two requests without headers get different IDs
        var middleware = new CorrelationIdMiddleware(next: _ => Task.CompletedTask);

        var context1 = new DefaultHttpContext();
        var context2 = new DefaultHttpContext();

        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        var id1 = context1.Response.Headers["X-Correlation-Id"].FirstOrDefault();
        var id2 = context2.Response.Headers["X-Correlation-Id"].FirstOrDefault();

        id1.Should().NotBe(id2, "each request needs a unique correlation ID");
    }
}
