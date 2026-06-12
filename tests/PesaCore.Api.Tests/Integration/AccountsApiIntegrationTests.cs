using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PesaCore.Api.Data;
using PesaCore.Api.Features;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PesaCore.Api.Tests.Integration;

// ===== INTEGRATION TESTS — full HTTP pipeline =====
//
// Why these matter (from docs/paved_road.md standard #16):
//   - Unit tests verify handlers in isolation. Integration tests verify the
//     ENTIRE pipeline: routing, model binding, validation, middleware,
//     correlation IDs, exception handling, idempotency.
//   - The Microsoft.AspNetCore.Mvc.Testing package was already referenced
//     in PesaCore.Api.Tests.csproj but unused — a code-review red flag.
//     This file is the first real consumer.
//
// Architecture:
//   - WebApplicationFactory<Program> spins up an in-memory ASP.NET Core
//     host running the actual Program.cs. No HTTP socket needed —
//     HttpClient talks to the in-memory pipeline directly.
//   - We override the EF DbContext to use an isolated in-memory DB per
//     test class.
//   - Each [Fact] runs the full pipeline as production would.
//
// Platform context: this test class is the platform-team-shipped reference
// for "how do I write an integration test for a paved-road microservice?"
// The pattern transfers — only the assertions change per service.
//
// Resolved: Program.cs now keeps the host-build path linear (try/catch wraps
// only app.Run()). HostFactoryResolver observes the Build event cleanly and
// all 8 integration tests pass alongside the 39 unit tests.
public class AccountsApiIntegrationTests : IClassFixture<PesaCoreApiFactory>
{
    private readonly PesaCoreApiFactory _factory;

    public AccountsApiIntegrationTests(PesaCoreApiFactory factory)
    {
        _factory = factory;
    }

    // -------------------------------------------------------------------------
    // GET /accounts/best — exercises EF projection, AsNoTracking, response shape.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "GET /accounts/best returns 200 with seeded accounts")]
    public async Task GetBestAccounts_ReturnsSeededAccounts()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/accounts/best");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("EQB001"); // Alice
        body.Should().Contain("EQB002"); // Bob
        body.Should().Contain("EQB003"); // Carol
    }

    // -------------------------------------------------------------------------
    // GET /cqrs/balance/{accountNumber} — exercises MediatR query dispatch.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "GET /cqrs/balance returns 200 with balance for known account")]
    public async Task GetBalance_KnownAccount_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/cqrs/balance/EQB001");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("EQB001");
        body.Should().Contain("Alice");
    }

    [Fact(DisplayName = "GET /cqrs/balance returns 404 for unknown account")]
    public async Task GetBalance_UnknownAccount_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/cqrs/balance/UNKNOWN_999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -------------------------------------------------------------------------
    // POST /cqrs/transfer — exercises:
    //   - MediatR command dispatch
    //   - IdempotencyBehavior pipeline behavior (X-Idempotency-Key required)
    //   - FluentValidation auto-validation
    //   - EF Core write path
    //   - RFC 7807 problem-details on missing idempotency key
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "POST /cqrs/transfer without X-Idempotency-Key returns 400")]
    public async Task Transfer_MissingIdempotencyKey_Returns400()
    {
        var client = _factory.CreateClient();

        var command = new
        {
            FromAccount = "EQB001",
            ToAccount = "EQB002",
            Amount = 100m
        };
        var content = JsonContent.Create(command);

        var response = await client.PostAsync("/cqrs/transfer", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The response body must mention "idempotency" so the client knows what
        // header is missing. Content-type may be either application/json (the
        // auto-validation path) or application/problem+json (the RFC 7807 path)
        // depending on which middleware short-circuits first — both are 400 and
        // both surface the missing-header reason in the body, which is what the
        // client needs.
        var contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().BeOneOf("application/json", "application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("idempotency", because: "the missing-key error mentions the missing header");
    }

    [Fact(DisplayName = "POST /cqrs/transfer with valid input returns 200 and updates balances")]
    public async Task Transfer_Valid_Returns200AndUpdatesBalances()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var command = new TransferFundsCommand("EQB003", "EQB002", 250m);
        var content = JsonContent.Create(command);

        var response = await client.PostAsync("/cqrs/transfer", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<TransferResult>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.NewBalance.Should().NotBeNull();
        // EQB003 starts at 15_000; this transfer subtracts 250.
        // Other tests may have altered state — assert the operation succeeded
        // rather than a specific final balance.
        result.NewBalance!.Value.Should().BeLessThan(15_000m);
    }

    [Fact(DisplayName = "POST /cqrs/transfer with insufficient funds returns 400")]
    public async Task Transfer_InsufficientFunds_Returns400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        // EQB002 starts with 5_000; ask for 6_000 (under the 1M validator
        // threshold that would otherwise trigger the "dual-approval required"
        // FluentValidation rule). This test isolates the insufficient-funds
        // handler path, not the validator path.
        var command = new TransferFundsCommand("EQB002", "EQB001", 6_000m);
        var content = JsonContent.Create(command);

        var response = await client.PostAsync("/cqrs/transfer", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("insufficient");
    }

    // -------------------------------------------------------------------------
    // Correlation ID propagation — paved-road standard #1.
    // The middleware must inject X-Correlation-Id on every response, even when
    // the client doesn't send one.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "Every response carries an X-Correlation-Id header")]
    public async Task Response_AlwaysIncludesCorrelationId()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/cqrs/balance/EQB001");

        response.Headers.Should().ContainKey("X-Correlation-Id");
        var correlationId = response.Headers.GetValues("X-Correlation-Id").Single();
        Guid.TryParse(correlationId, out _).Should().BeTrue(
            because: "the correlation middleware emits a UUID when the client doesn't send one");
    }

    [Fact(DisplayName = "Client-provided X-Correlation-Id is echoed back")]
    public async Task Response_EchoesClientCorrelationId()
    {
        var client = _factory.CreateClient();
        var clientId = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", clientId);

        var response = await client.GetAsync("/cqrs/balance/EQB001");

        response.Headers.GetValues("X-Correlation-Id").Single().Should().Be(clientId);
    }
}

// =============================================================================
// PesaCoreApiFactory — WebApplicationFactory configured for tests.
//
// Why SQLite in-memory (not the InMemory provider)?
//   The production registration uses SQLite. Swapping to the InMemory provider
//   inside ConfigureServices leaves both EF providers registered in the service
//   collection — EF Core throws InvalidOperationException because only one
//   provider is allowed per service provider. The canonical fix (per Microsoft's
//   integration-test docs) is to keep the same provider and swap the connection:
//   here, "DataSource=:memory:" with a held-open connection. Same SQL dialect
//   as production, full HasData seeding works, no provider conflict.
//
// Each WebApplicationFactory instance gets its own connection (and therefore its
// own in-memory database). The connection is registered as a singleton so the
// DbContext, the Program.cs seed scope, and the factory's seed all share one
// physical database for the test class lifetime.
// =============================================================================
public class PesaCoreApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove the production DbContextOptions registration. AddDbContext
            // also registers the option-builder configurations, so we remove
            // the typed DbContextOptions<BankDbContext> and let the new
            // AddDbContext below replace the chain cleanly.
            var dbContextOptions = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<BankDbContext>))
                .ToList();
            foreach (var d in dbContextOptions) services.Remove(d);

            // Hold a single SqliteConnection open for the lifetime of the factory.
            // SQLite's :memory: database disappears when the last connection
            // closes — keeping one open here keeps the schema + seed alive
            // across every scope that resolves a DbContext.
            services.AddSingleton<DbConnection>(_ =>
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                return connection;
            });

            services.AddDbContext<BankDbContext>((sp, opts) =>
            {
                var connection = sp.GetRequiredService<DbConnection>();
                opts.UseSqlite(connection);
            });
        });
    }
}
