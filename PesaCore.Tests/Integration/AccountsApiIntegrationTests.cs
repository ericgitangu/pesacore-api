using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PesaCore.Data;
using PesaCore.Features;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PesaCore.Tests.Integration;

// ===== INTEGRATION TESTS — full HTTP pipeline =====
//
// Why these matter (from the platform paved-road standards):
//   - Unit tests verify handlers in isolation. Integration tests verify the
//     ENTIRE pipeline: routing, model binding, validation, middleware,
//     correlation IDs, exception handling, idempotency.
//
// HONEST AGAINST REAL DATA (no seed): the app no longer seeds Alice/Bob/Carol.
// The DB starts EMPTY, so each test CREATES the accounts it needs through the real
// POST /Cqrs/accounts endpoint and then asserts — create-then-assert, end-to-end.
// Account numbers are allocated server-side (EQB001, EQB002, ...), so the helper
// returns the created numbers rather than hard-coding them.
//
// Architecture:
//   - WebApplicationFactory<Program> spins up an in-memory ASP.NET Core host running
//     the actual Program.cs. HttpClient talks to the in-memory pipeline directly.
//   - EF DbContext uses an isolated SQLite :memory: DB per factory (same provider as
//     prod's SQLite local path, full schema + index enforcement).
//
// Each test class gets its OWN factory (NOT IClassFixture-shared) so the empty-DB
// starting state is deterministic and create-then-assert sequences don't interfere
// across tests through shared state.
public class AccountsApiIntegrationTests
{
    // Create an account through the real endpoint and return its server-allocated number.
    private static async Task<string> CreateAccountAsync(
        HttpClient client, string holder, decimal opening)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/cqrs/accounts")
        {
            Content = JsonContent.Create(new CreateAccountCommand(holder, opening))
        };
        msg.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var resp = await client.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            because: $"opening {holder} should succeed; body={await resp.Content.ReadAsStringAsync()}");
        var result = await resp.Content.ReadFromJsonAsync<CreateAccountResult>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result!.AccountNumber.Should().NotBeNullOrEmpty();
        return result.AccountNumber!;
    }

    // -------------------------------------------------------------------------
    // POST /cqrs/accounts — opens an account, allocates EQB001 on an empty ledger.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "POST /cqrs/accounts opens the first account as EQB001")]
    public async Task CreateAccount_OnEmptyLedger_AllocatesEqb001()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();

        var number = await CreateAccountAsync(client, "Dana", 2_500m);

        number.Should().Be("EQB001");
    }

    // -------------------------------------------------------------------------
    // GET /accounts/dto/linq — the dashboard's source. Empty DB -> created accounts.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "GET /accounts/dto/linq returns only the accounts that were created")]
    public async Task GetLinqAccounts_ReturnsCreatedAccounts()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();

        // Empty to begin with — the dashboard would render the empty state.
        var empty = await client.GetFromJsonAsync<List<JsonElement>>("/accounts/dto/linq");
        empty!.Should().BeEmpty();

        var a = await CreateAccountAsync(client, "Dana", 10_000m);
        var b = await CreateAccountAsync(client, "Evan", 5_000m);

        var response = await client.GetAsync("/accounts/dto/linq");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(a).And.Contain("Dana");
        body.Should().Contain(b).And.Contain("Evan");
        body.Should().Contain("10000"); // Dana's persisted balance
    }

    // -------------------------------------------------------------------------
    // GET /cqrs/balance/{accountNumber} — MediatR query against a created account.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "GET /cqrs/balance returns 200 for a created account")]
    public async Task GetBalance_CreatedAccount_Returns200()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();
        var number = await CreateAccountAsync(client, "Dana", 7_777m);

        var response = await client.GetAsync($"/cqrs/balance/{number}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(number).And.Contain("Dana").And.Contain("7777");
    }

    [Fact(DisplayName = "GET /cqrs/balance returns 404 for unknown account")]
    public async Task GetBalance_UnknownAccount_Returns404()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/cqrs/balance/UNKNOWN_999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -------------------------------------------------------------------------
    // POST /cqrs/transfer — full create-then-transfer end-to-end:
    //   - MediatR command dispatch
    //   - IdempotencyBehavior (X-Idempotency-Key required)
    //   - FluentValidation auto-validation
    //   - EF Core write path against REAL persisted accounts
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "POST /cqrs/transfer without X-Idempotency-Key returns 400")]
    public async Task Transfer_MissingIdempotencyKey_Returns400()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();

        var command = new { FromAccount = "EQB001", ToAccount = "EQB002", Amount = 100m };
        var response = await client.PostAsync("/cqrs/transfer", JsonContent.Create(command));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().BeOneOf("application/json", "application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("idempotency",
            because: "the missing-key error mentions the missing header");
    }

    [Fact(DisplayName = "Create two accounts then transfer: 200 and balances update")]
    public async Task Transfer_AfterCreate_Returns200AndUpdatesBalances()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();
        var from = await CreateAccountAsync(client, "Dana", 15_000m);
        var to = await CreateAccountAsync(client, "Evan", 5_000m);

        client.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());
        var command = new TransferFundsCommand(from, to, 250m);

        var response = await client.PostAsync("/cqrs/transfer", JsonContent.Create(command));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonSerializer.Deserialize<TransferResult>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.NewBalance.Should().Be(14_750m); // Dana: 15_000 - 250
    }

    [Fact(DisplayName = "Transfer with insufficient funds returns 400 against a created account")]
    public async Task Transfer_InsufficientFunds_Returns400()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();
        var from = await CreateAccountAsync(client, "Dana", 5_000m);
        var to = await CreateAccountAsync(client, "Evan", 0m);

        client.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());
        var command = new TransferFundsCommand(from, to, 6_000m);

        var response = await client.PostAsync("/cqrs/transfer", JsonContent.Create(command));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("insufficient");
    }

    // -------------------------------------------------------------------------
    // Correlation ID propagation — paved-road standard #1.
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "Every response carries an X-Correlation-Id header")]
    public async Task Response_AlwaysIncludesCorrelationId()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/cqrs/balance/EQB001");

        response.Headers.Should().ContainKey("X-Correlation-Id");
        var correlationId = response.Headers.GetValues("X-Correlation-Id").Single();
        Guid.TryParse(correlationId, out _).Should().BeTrue(
            because: "the correlation middleware emits a UUID when the client doesn't send one");
    }

    [Fact(DisplayName = "Client-provided X-Correlation-Id is echoed back")]
    public async Task Response_EchoesClientCorrelationId()
    {
        using var factory = new PesaCoreApiFactory();
        var client = factory.CreateClient();
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
//   inside ConfigureServices leaves both EF providers registered — EF Core throws
//   because only one provider is allowed per service provider. The canonical fix
//   (per Microsoft's integration-test docs) is to keep the same provider and swap
//   the connection: "DataSource=:memory:" with a held-open connection. Same SQL
//   dialect as production, schema + unique-index enforcement, no provider conflict.
//
// NO SEED: the schema is created empty (Program.cs no longer seeds). Tests create
// the accounts they need through the real API.
//
// Each factory instance gets its own connection (and therefore its own in-memory
// database). The connection is a singleton so the DbContext and the Program.cs
// schema-bootstrap scope share one physical database for the factory lifetime.
// =============================================================================
public class PesaCoreApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            var dbContextOptions = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<BankDbContext>))
                .ToList();
            foreach (var d in dbContextOptions) services.Remove(d);

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
