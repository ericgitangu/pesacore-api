// ===== PesaCore.Web — Backend-for-Frontend (BFF) host =====
//
// Two responsibilities, one process:
//   1. Serve the Blazor WebAssembly client (static .wasm/.js/.dll payload).
//   2. Reverse-proxy /api/* to the PesaCore API via YARP, keeping the API
//      base URL server-side. The browser only ever talks to THIS origin, so
//      there is no CORS and no API URL/secret in client code.
//
// The PesaCore base URL is config-driven (PesaCore:BaseUrl):
//   - local dotnet : http://localhost:5235   (appsettings.Development.json)
//   - docker       : http://pesacore:8080    (PesaCore__BaseUrl env, compose)
//   - Cloud Run    : injected as PesaCore__BaseUrl env var
//
// Cloud Run contract: listen on $PORT (defaults to 8080). Set below.

using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Cloud Run / container: honor $PORT, default 8080. Local dev uses launchSettings.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://+:{port}");
}

// --- YARP reverse proxy, configured in code from PesaCore:BaseUrl ---
// We build the route/cluster in code (rather than the appsettings ReverseProxy
// section) so the destination address is derived from a single config key and
// validated at startup. Route: /api/{**catch-all} -> {PesaCore:BaseUrl}/{**}.
// The "/api" prefix is stripped so /api/Accounts/best hits PesaCore /Accounts/best.
var apiBaseUrl = builder.Configuration["PesaCore:BaseUrl"] ?? "http://localhost:5235";

var routes = new[]
{
    new RouteConfig
    {
        RouteId = "pesacore-api",
        ClusterId = "pesacore-cluster",
        Match = new RouteMatch { Path = "/api/{**catch-all}" },
        // Strip the /api prefix before forwarding upstream.
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
            ["pesacore"] = new DestinationConfig { Address = apiBaseUrl }
        }
    }
};

builder.Services
    .AddReverseProxy()
    .LoadFromMemory(routes, clusters);

builder.Services.AddHealthChecks();

// Response compression for the SPA payload + CSS over HTTP. Blazor's
// publish step also emits precompressed .br/.gz for the _framework assets,
// which UseBlazorFrameworkFiles serves directly; this covers everything else.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

var app = builder.Build();

app.UseResponseCompression();

// --- Middleware pipeline ---
// Serve the Blazor WASM framework files (.wasm, .js, blazor.boot.json, etc.).
app.UseBlazorFrameworkFiles();
// Serve everything else under wwwroot (index.html, css, icons).
app.UseStaticFiles();

app.UseRouting();

// Liveness/readiness probe — a real route, used by Docker HEALTHCHECK + Cloud Run.
app.MapHealthChecks("/healthz");

// Proxy /api/* to PesaCore. Registered before the SPA fallback so API calls
// are never swallowed by MapFallbackToFile.
app.MapReverseProxy();

// ReDoc API reference at /docs — native ASP.NET Core endpoint serving the ReDoc
// renderer, pointed at the API's OpenAPI spec proxied through this BFF
// (/api/openapi/v1.json -> YARP -> PesaCore /openapi/v1.json, same origin, no CORS).
// Registered before the SPA fallback so /docs isn't swallowed by MapFallbackToFile.
app.MapGet("/docs", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>PesaCore API — Reference</title>
      <style>body { margin: 0; padding: 0; }</style>
    </head>
    <body>
      <redoc spec-url="/api/openapi/v1.json"
             theme='{"colors":{"primary":{"main":"#3ddc97"}},"typography":{"fontFamily":"IBM Plex Sans, system-ui, sans-serif","code":{"fontFamily":"IBM Plex Mono, monospace"}}}'></redoc>
      <script src="https://cdn.redoc.ly/redoc/latest/bundles/redoc.standalone.js"></script>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));

// SPA fallback: unmatched routes return index.html for client-side routing.
// The served index.html is the CLIENT's standalone publish (overlaid in Dockerfile.web)
// with (a) the populated <script type="importmap"> the WASM runtime needs to resolve
// fingerprinted module assets, and (b) the boot <script src> hash baked in at build
// (sed) — because neither publish nor a classic <script src>+import-map resolves the
// `#[.{fingerprint}]` placeholder. See docs/issues_caught_and_resolved.md #1.
app.MapFallbackToFile("index.html");

app.Run();

// Exposed for potential WebApplicationFactory integration tests.
public partial class Program { }
