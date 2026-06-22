using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PesaCore.Data;
using PesaCore.Features;
using Yarp.ReverseProxy.Configuration;

namespace PesaCore.Tests.Integration;

// ===== BFF PROXY INTEGRATION TESTS — proves the real YARP path =====
//
// `dotnet run` is unavailable in the build sandbox, so to exercise the REAL
// proxy code path (not a deduction) we:
//   1. Boot a real PesaCore API on a real loopback Kestrel port (so YARP has a
//      genuine HTTP upstream to forward to — TestServer's in-memory handler
//      can't be a YARP destination).
//   2. Boot a SECOND host with the exact same YARP route/cluster wiring as
//      PesaCore.Web/Program.cs, pointed at that upstream via PesaCore:BaseUrl.
//   3. Hit the BFF over real HTTP and assert the proxied responses.
//
// This is the same /api/* -> {BaseUrl}/* + PathRemovePrefix("/api") config the
// production BFF uses, so a green test here confirms the contract the WASM
// client depends on. Both hosts are disposed in DisposeAsync.
public class BffProxyIntegrationTests : IAsyncLifetime
{
    private WebApplication _api = null!;     // PesaCore upstream
    private WebApplication _bff = null!;     // YARP gateway
    private string _apiBaseUrl = "";
    private string _bffBaseUrl = "";
    private SqliteConnection _conn = null!;

    public async Task InitializeAsync()
    {
        // ---- upstream: real PesaCore pipeline on a real port ----
        // SQLite :memory: held-open-connection trick — the schema is created EMPTY
        // (no seed any more). Tests open the accounts they need through the real
        // POST /api/Cqrs/accounts proxy path before asserting (create-then-assert).
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Configuration["AllowedHosts"] = "*";
        apiBuilder.WebHost.UseUrls("http://127.0.0.1:0"); // :0 => OS picks a free port
        // Controllers live in the PesaCore assembly, not this test assembly —
        // register that assembly as an application part so routing finds them.
        apiBuilder.Services.AddControllers()
            .AddApplicationPart(typeof(BankDbContext).Assembly);
        // FluentValidation auto-validation — mirror Program.cs so CreateAccountValidator
        // (and TransferFundsValidator) run in the proxied pipeline as they do in prod.
        apiBuilder.Services.AddFluentValidationAutoValidation();
        apiBuilder.Services.AddValidatorsFromAssemblyContaining<
            PesaCore.Validators.TransferFundsValidator>();
        apiBuilder.Services.AddDbContext<BankDbContext>(o => o.UseSqlite(_conn));
        apiBuilder.Services.AddHttpContextAccessor();
        // ADR 0002: IdempotencyBehavior + the cache-aside read path now depend on
        // IDistributedCache. Register the in-memory fallback (same as Program.cs when
        // no Redis is configured) so DI can resolve the behavior in this test host.
        apiBuilder.Services.AddDistributedMemoryCache();
        // AutoMapper + Finacle client are constructor deps of AccountsController.
        apiBuilder.Services.AddAutoMapper(cfg =>
            cfg.AddMaps(typeof(BankDbContext).Assembly));
        apiBuilder.Services.AddSingleton<
            PesaCore.Services.IFinacleClient, PesaCore.Services.FinacleClient>();
        // MediatR + the idempotency pipeline behavior (the keyless-400 path).
        apiBuilder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(BankDbContext).Assembly);
            cfg.AddOpenBehavior(typeof(PesaCore.Behaviors.IdempotencyBehavior<,>));
        });

        _api = apiBuilder.Build();
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BankDbContext>();
            db.Database.EnsureCreated();
        }
        // Map MissingIdempotencyKeyException -> 400 exactly as PesaCore/Program.cs does.
        _api.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
        {
            var ex = ctx.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var status = ex is PesaCore.Behaviors.MissingIdempotencyKeyException ? 400 : 500;
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsJsonAsync(new { title = ex?.Message, status });
        }));
        _api.MapControllers();
        await _api.StartAsync();
        _apiBaseUrl = ResolvedAddress(_api);

        // ---- BFF: identical YARP wiring to PesaCore.Web/Program.cs ----
        var bffBuilder = WebApplication.CreateBuilder();
        bffBuilder.Configuration["AllowedHosts"] = "*";
        bffBuilder.WebHost.UseUrls("http://127.0.0.1:0");

        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "pesacore-api",
                ClusterId = "pesacore-cluster",
                Match = new RouteMatch { Path = "/api/{**catch-all}" },
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/api" }
                }
            }
        };
        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "pesacore-cluster",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["pesacore"] = new DestinationConfig { Address = _apiBaseUrl }
                }
            }
        };
        bffBuilder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

        _bff = bffBuilder.Build();
        _bff.MapReverseProxy();
        await _bff.StartAsync();
        _bffBaseUrl = ResolvedAddress(_bff);
    }

    // _api.Urls echoes the *requested* URL ("http://127.0.0.1:0"); the resolved
    // port lives in the server's IServerAddressesFeature after StartAsync.
    private static string ResolvedAddress(WebApplication app)
    {
        var feature = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        return feature!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _bff.StopAsync();
        await _api.StopAsync();
        await _bff.DisposeAsync();
        await _api.DisposeAsync();
        _conn.Dispose();
    }

    private HttpClient Bff() => new() { BaseAddress = new Uri(_bffBaseUrl) };

    // Open an account through the BFF (POST /api/Cqrs/accounts) and return its
    // server-allocated number — exercises the real proxied create path.
    private static async Task<string> CreateViaBffAsync(HttpClient client, string holder, decimal opening)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/Cqrs/accounts")
        {
            Content = JsonContent.Create(new CreateAccountCommand(holder, opening))
        };
        msg.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var resp = await client.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            because: $"opening {holder} via BFF should succeed; body={await resp.Content.ReadAsStringAsync()}");
        var result = await resp.Content.ReadFromJsonAsync<CreateAccountResult>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result!.AccountNumber.Should().NotBeNullOrEmpty();
        return result.AccountNumber!;
    }

    [Fact(DisplayName = "BFF proxies POST /api/Cqrs/accounts then GET /api/Accounts/best shows it")]
    public async Task Proxy_CreateThenList_ShowsCreatedAccount()
    {
        // Sanity: the upstream itself must answer directly first (empty ledger).
        using (var direct = new HttpClient { BaseAddress = new Uri(_apiBaseUrl) })
        {
            var d = await direct.GetAsync("/Accounts/best");
            var db = await d.Content.ReadAsStringAsync();
            d.StatusCode.Should().Be(HttpStatusCode.OK,
                because: $"upstream {_apiBaseUrl} should answer directly; body={db}");
        }

        using var client = Bff();

        // Create-then-assert end-to-end through the proxy.
        var number = await CreateViaBffAsync(client, "Dana", 12_000m);

        var resp = await client.GetAsync("/api/Accounts/best");
        var diag = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"BFF {_bffBaseUrl} -> {_apiBaseUrl}; body={diag}");
        diag.Should().Contain(number).And.Contain("Dana");
    }

    [Fact(DisplayName = "BFF proxies GET /api/Accounts/dto/linq with balance (dashboard source)")]
    public async Task Proxy_GetAccountsLinq_IncludesBalance()
    {
        using var client = Bff();
        var number = await CreateViaBffAsync(client, "Dana", 10_000m);

        var resp = await client.GetAsync("/api/Accounts/dto/linq");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("balance");
        body.Should().Contain(number);
        body.Should().Contain("10000"); // Dana's persisted balance
    }

    [Fact(DisplayName = "BFF proxies POST /api/Cqrs/transfer with X-Idempotency-Key end-to-end")]
    public async Task Proxy_Transfer_WithIdempotencyKey_Succeeds()
    {
        using var client = Bff();
        var from = await CreateViaBffAsync(client, "Dana", 15_000m);
        var to = await CreateViaBffAsync(client, "Evan", 5_000m);

        using var txClient = Bff();
        txClient.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());

        var resp = await txClient.PostAsJsonAsync(
            "/api/Cqrs/transfer", new TransferFundsCommand(from, to, 250m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<TransferResult>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.NewBalance.Should().Be(14_750m); // 15_000 - 250
    }

    [Fact(DisplayName = "EVIDENCE: capture literal proxied HTTP codes + JSON (create -> list -> transfer)")]
    public async Task Evidence_Capture()
    {
        using var client = Bff();
        var sb = new System.Text.StringBuilder();

        // Create-then-use: capture the SERVER-allocated numbers instead of assuming
        // EQB001/EQB002. The DB is class-shared and never reset, so account allocation is
        // order-dependent across tests — hardcoded literals would point at the wrong (or
        // missing) accounts. CreateViaBffAsync already records the create succeeded.
        var fromAccount = await CreateViaBffAsync(client, "Dana", 15_000m);
        sb.AppendLine($"POST /api/Cqrs/accounts (+key) -> 201 ({fromAccount})");
        var toAccount = await CreateViaBffAsync(client, "Evan", 5_000m);
        sb.AppendLine($"POST /api/Cqrs/accounts (+key) -> 201 ({toAccount})");

        var best = await client.GetAsync("/api/Accounts/best");
        sb.AppendLine($"GET /api/Accounts/best -> {(int)best.StatusCode}");
        sb.AppendLine(await best.Content.ReadAsStringAsync());

        var linq = await client.GetAsync("/api/Accounts/dto/linq");
        sb.AppendLine($"GET /api/Accounts/dto/linq -> {(int)linq.StatusCode}");
        sb.AppendLine(await linq.Content.ReadAsStringAsync());

        using var txClient = Bff();
        txClient.DefaultRequestHeaders.Add("X-Idempotency-Key", Guid.NewGuid().ToString());
        var tx = await txClient.PostAsJsonAsync(
            "/api/Cqrs/transfer", new TransferFundsCommand(fromAccount, toAccount, 250m));
        sb.AppendLine($"POST /api/Cqrs/transfer (+key) -> {(int)tx.StatusCode}");
        sb.AppendLine(await tx.Content.ReadAsStringAsync());

        await File.WriteAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "proxy_evidence.txt"), sb.ToString());
        best.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "BFF serves the WASM host page (GET / -> branded index.html)")]
    public async Task Bff_ServesSpaHostPage()
    {
        // Locate the published SPA wwwroot (produced by `dotnet publish PesaCore.Web`).
        // If it isn't present in this run, skip rather than fail spuriously.
        var wwwroot = FindPublishedWwwroot();
        if (wwwroot is null) return;

        var host = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = wwwroot });
        host.Configuration["AllowedHosts"] = "*";
        host.WebHost.UseUrls("http://127.0.0.1:0");
        var spa = host.Build();
        spa.UseStaticFiles();
        spa.UseRouting();
        spa.MapFallbackToFile("index.html");
        await spa.StartAsync();
        try
        {
            var addr = ResolvedAddress(spa);
            using var client = new HttpClient { BaseAddress = new Uri(addr) };

            var root = await client.GetAsync("/");
            root.StatusCode.Should().Be(HttpStatusCode.OK);
            var html = await root.Content.ReadAsStringAsync();
            html.Should().Contain("<!DOCTYPE html>");
            html.Should().Contain("PesaCore");                    // branded host page
            html.Should().Contain("blazor.webassembly");          // WASM bootstrap

            // Deep link falls back to the SPA host page (client-side routing).
            var deep = await client.GetAsync("/accounts/EQB001");
            deep.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await spa.StopAsync();
            await spa.DisposeAsync();
        }
    }

    private static string? FindPublishedWwwroot()
    {
        // Walk up to the repo root, then probe the known publish output locations.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            foreach (var cand in new[]
            {
                Path.Combine(dir, "PesaCore.Web", "bin", "Release", "net10.0", "wwwroot"),
                Path.Combine(dir, "PesaCore.Web", "bin", "Debug", "net10.0", "wwwroot"),
                Path.Combine(dir, "PesaCore.Web.Client", "bin", "Release", "net10.0", "wwwroot"),
                Path.Combine(dir, "PesaCore.Web.Client", "bin", "Debug", "net10.0", "wwwroot"),
            })
            {
                if (File.Exists(Path.Combine(cand, "index.html"))) return cand;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    [Fact(DisplayName = "BFF proxies a keyless POST and surfaces PesaCore's 400")]
    public async Task Proxy_Transfer_MissingKey_Returns400()
    {
        using var client = Bff();

        var resp = await client.PostAsJsonAsync(
            "/api/Cqrs/transfer", new TransferFundsCommand("EQB001", "EQB002", 100m));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("idempotency");
    }
}
