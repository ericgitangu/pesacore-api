using Microsoft.EntityFrameworkCore;
using Serilog;
using FluentValidation;
using FluentValidation.AspNetCore;
using MediatR;
using PesaCore.Api.Data;
using PesaCore.Api.Services;
using PesaCore.Api.Middleware;
using PesaCore.Api.Validators;
using PesaCore.Api.Behaviors;
using Scalar.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ===== SERILOG BOOTSTRAP — configured BEFORE the host builder =====
// Why before? If the host fails to start (bad config, missing dependency),
// you need logging already working to capture WHY it failed.
// Log.Logger is Serilog's global static logger — available everywhere.
//
// Enrich.FromLogContext(): pulls properties pushed via LogContext.PushProperty()
// (that's how the CorrelationId middleware injects the ID into every log line).
// Java equivalent: SLF4J + Logback with MDC (Mapped Diagnostic Context).
//
// The output template includes {CorrelationId} — every log line shows the request's
// correlation ID in brackets. If no ID is set (e.g., during startup), it's blank.
// In production you'd add .WriteTo.Seq() or .WriteTo.ElasticSearch() for centralized logging.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// ===== HOST BUILD PATH — LINEAR, NOT WRAPPED IN try/catch =====
// Why no try/catch around CreateBuilder/Build/middleware?
// WebApplicationFactory<Program> in integration tests uses HostFactoryResolver,
// which observes a DiagnosticListener event fired during builder.Build(). When
// that path is wrapped in a try block, the test host's stop signal cannot
// propagate cleanly and the listener throws "The entry point exited without
// ever building an IHost." The fix (per Microsoft and serilog-aspnetcore #289):
// keep the build path linear; wrap only app.Run() in try/catch/finally.
//
// Startup-failure logging is preserved by the wrapping try around app.Run() —
// any exception thrown from Build() or middleware setup will still be caught
// at app.Run() (the host raises pending startup exceptions when StartAsync runs).

var builder = WebApplication.CreateBuilder(args);

// Replace the default ASP.NET Core logger with Serilog.
// All ILogger<T> injections now route through Serilog.
// This is a single line that replaces the entire logging infrastructure.
builder.Host.UseSerilog();

// --- Service registration (DI container) ---

// MVC controller support — the .NET 10 scaffold uses minimal APIs (MapGet) by default,
// but we're adding controller-based routing so AccountsController works.
// AddControllers() scans the assembly for classes inheriting ControllerBase,
// registers them as transient services, and wires up model binding + validation.
builder.Services.AddControllers();

// CORS — must register the service before using the middleware.
// Empty AddCors() = deny-all default. Production would call AddPolicy() with
// specific origins, methods, and headers for the bank's frontend domains.
builder.Services.AddCors();

// OpenAPI/Swagger doc generation — came with the scaffold
builder.Services.AddOpenApi();

// Finacle client — Singleton because the only mutable state is an atomic counter.
// In production with a real HttpClient you'd use AddHttpClient<IFinacleClient, FinacleClient>()
// which registers a typed HttpClient factory — avoids socket exhaustion (the DNS/connection
// pooling problem that plagued early .NET HttpClient usage).
// Java equivalent: @Bean @Singleton RestTemplate or WebClient
builder.Services.AddSingleton<IFinacleClient, FinacleClient>();

// IHttpContextAccessor — provides access to the current HttpContext outside of controllers.
// Needed by IdempotencyBehavior to read the X-Idempotency-Key header from the MediatR pipeline.
// Singleton service — the accessor itself is a singleton, but HttpContext is per-request.
// Java equivalent: RequestContextHolder.getRequestAttributes() in Spring
// Python equivalent: Flask's request context (flask.request) or Starlette's Request
builder.Services.AddHttpContextAccessor();

// MediatR — in-process message dispatcher for CQRS pattern.
// RegisterServicesFromAssembly scans for all IRequestHandler<,> implementations
// and registers them as Transient (new instance per Send() call).
// IMediator itself is Transient; the underlying ServiceFactory uses the DI scope.
// AddOpenBehavior registers a pipeline behavior that wraps EVERY handler —
// IdempotencyBehavior intercepts commands, checks/caches idempotency keys,
// and prevents double-submit. Pipeline order: Idempotency → Validation → Handler.
// Java equivalent: Axon Framework's CommandGateway/QueryGateway bean registration.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
});

// AutoMapper — scans this assembly for all Profile subclasses (e.g., AccountProfile),
// builds an immutable MapperConfiguration, and registers IMapper as Singleton.
// Singleton because the config is built once, is thread-safe, and never changes.
// Java equivalent: MapStruct (compile-time) or ModelMapper (runtime) bean registration.
builder.Services.AddAutoMapper(cfg => cfg.AddMaps(typeof(Program).Assembly));

// FluentValidation — separates validation rules from DTOs/Commands.
// AddFluentValidationAutoValidation: hooks into ASP.NET Core model binding —
// validators run AUTOMATICALLY before the action/handler executes.
// If validation fails, the framework returns 400 with error details.
// AddValidatorsFromAssemblyContaining: scans for all AbstractValidator<T> subclasses.
// Java equivalent: javax.validation / Hibernate Validator with @Valid annotation.
// Python equivalent: Pydantic validators or Marshmallow schema validation.
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<TransferFundsValidator>();

// EF Core — SQLite for learning. Switched from InMemoryDatabase because:
//   1. Dapper (Step 6) needs a REAL SQL connection — InMemory is a LINQ-to-Objects translator
//   2. SQLite generates actual SQL so you can see JOIN/subquery differences in logs
//   3. Production would be UseSqlServer (Azure SQL) or UseNpgsql (Postgres)
// "Data Source=BankDb.db" creates a file-based SQLite database in the project root.
// EnableSensitiveDataLogging: includes parameter values in logs (dev only — never in prod).
// LogTo(Console.WriteLine): prints every SQL query EF generates to the terminal.
builder.Services.AddDbContext<BankDbContext>(options =>
    options.UseSqlite("Data Source=BankDb.db")
           .EnableSensitiveDataLogging()
           .LogTo(Console.WriteLine,
                  Microsoft.Extensions.Logging.LogLevel.Information));

// ===== OpenTelemetry — distributed tracing + metrics =====
// See docs/paved_road.md standard #12. The platform-wide standard is:
// every service emits OTel traces and metrics with a consistent service name
// and resource attributes, exported via OTLP to a collector.
//
// Resource attributes identify the service in the telemetry backend.
// Service.name: how this service shows up in App Insights / Jaeger / Tempo.
// Service.version: from assembly version; lets you correlate telemetry to release.
// deployment.environment: dev/staging/prod — separates noise.
//
// Instrumentation packages auto-instrument hot paths:
//   - AspNetCore: every HTTP request becomes a span
//   - HttpClient: every outbound HTTP call becomes a child span
//   - EntityFrameworkCore: every SQL query becomes a span (parameter values redacted)
//   - Runtime: GC, thread pool, JIT metrics
//
// OTLP exporter ships traces+metrics over gRPC to the collector. In Platform's
// hybrid environment this is configurable per environment (on-prem collector
// vs Application Insights direct).
//
// Java equivalent: OpenTelemetry Java SDK + javaagent. Same wire protocol.
// Python equivalent: opentelemetry-distro auto-instrumentation.
var otelServiceName = builder.Configuration["OpenTelemetry:ServiceName"]
                      ?? "pesacore-api";
var otelEndpoint = builder.Configuration["OpenTelemetry:ExporterEndpoint"];
var otelSamplingRatio = builder.Configuration.GetValue<double?>("OpenTelemetry:SamplingRatio")
                        ?? 1.0;

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: otelServiceName,
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["service.namespace"] = "pesacore",
            ["host.name"] = Environment.MachineName
        }))
    .WithTracing(tracing =>
    {
        tracing
            .SetSampler(new TraceIdRatioBasedSampler(otelSamplingRatio))
            .AddAspNetCoreInstrumentation(opt =>
            {
                // Don't trace the health check or static-asset paths — noise.
                opt.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments("/healthz") &&
                    !ctx.Request.Path.StartsWithSegments("/scalar") &&
                    !ctx.Request.Path.StartsWithSegments("/openapi");
                opt.RecordException = true;
            })
            .AddHttpClientInstrumentation(opt =>
            {
                opt.RecordException = true;
            });

        // Console exporter in development for local debugging.
        // OTLP exporter in any environment with a collector endpoint configured.
        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }

        if (!string.IsNullOrWhiteSpace(otelEndpoint))
        {
            tracing.AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(otelEndpoint);
            });
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation();

        if (builder.Environment.IsDevelopment())
        {
            metrics.AddConsoleExporter();
        }

        if (!string.IsNullOrWhiteSpace(otelEndpoint))
        {
            metrics.AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(otelEndpoint);
            });
        }
    });

var app = builder.Build();

// --- Middleware pipeline ---
// ORDER MATTERS. ASP.NET Core runs middleware top-to-bottom on request,
// bottom-to-top on response. Think of it as an onion — each layer wraps the next.
// Wrong order = silent bugs (e.g., CORS headers missing, auth bypassed).

// Correlation ID middleware — FIRST in the pipeline so every downstream middleware
// and handler has the ID available in Serilog's LogContext.
// Must be before UseExceptionHandler so exception logs also have the correlation ID.
app.UseMiddleware<CorrelationIdMiddleware>();

// Global exception handler — catches unhandled exceptions and returns a
// RFC 7807 Problem Details JSON response instead of leaking stack traces.
// In banking: stack traces in responses are a security finding (information disclosure).
// Now that Serilog is configured, exceptions are also logged with correlation IDs.
//
// Typed exception mapping: different exception types → different HTTP status codes.
// MissingIdempotencyKeyException → 400 (client error — missing required header).
// Everything else → 500 (server error — unexpected failure).
// Java equivalent: @ControllerAdvice with @ExceptionHandler methods per exception type.
// Python equivalent: FastAPI exception_handler registrations per exception class.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exceptionFeature = context.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;

        // Map typed exceptions to appropriate HTTP status codes.
        // Banking pattern: business rule violations → 400, not 500.
        // 500 should mean "something the server didn't expect" — not "you forgot a header."
        //
        // Switch expression on exception type — C#'s pattern matching.
        // Each arm returns a (int, string) tuple. The explicit cast on the
        // first arm tells the compiler the return type — without it, C# can't
        // infer the tuple type across arms (a known limitation of switch expressions
        // with deconstruction — the right-hand side must have a concrete type).
        // Java equivalent: instanceof chain in a catch block
        // Python equivalent: match/case on exception type (3.10+)
        var (statusCode, title) = exception switch
        {
            PesaCore.Api.Behaviors.MissingIdempotencyKeyException ex
                => ((int, string))(400, ex.Message),
            _ => (500, "An unexpected error occurred")
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc7807",
            title,
            status = statusCode
        });
    });
});

// HSTS — tells browsers "only talk to me over HTTPS for the next year."
// Banking requirement: CBK prudential guidelines mandate encrypted transport.
// Not sent in Development to avoid locking localhost to HTTPS.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// CORS — Cross-Origin Resource Sharing.
// Default policy: deny all cross-origin requests. In production you'd
// AllowSpecificOrigins for the frontend domain (e.g., app.equitybank.co.ke).
// Without CORS middleware, browser-based API consumers get blocked silently.
app.UseCors();

// Serilog request logging — replaces the verbose default ASP.NET Core request logging
// with a single structured log line per request including method, path, status, duration.
// This is the "access log" equivalent — one line per request, not five.
app.UseSerilogRequestLogging();

// Swagger/OpenAPI — dev only, never expose API docs in production banking.
// MapOpenApi() serves the raw OpenAPI JSON at /openapi/v1.json.
// MapScalarApiReference() adds an interactive UI at /scalar/v1 —
// browse endpoints, try requests, see schemas. Scalar is the .NET 10
// recommended replacement for Swashbuckle (which was removed from the default template).
// In production banking, API docs are internal-only (behind VPN or removed entirely).
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Map attribute-routed controllers ([Route], [HttpGet], etc.)
// Without this, controller endpoints won't be reachable even though they're registered.
app.MapControllers();

// --- Scaffold demo: minimal API weather endpoint ---
// This is the .NET 10 "minimal API" style — no controller class, just a lambda.
// Contrast with AccountsController which uses the traditional controller pattern.
// Both styles coexist fine; Equity's codebase likely uses controllers for structure.
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

// --- DB seed ---
// Scoped services (like DbContext) need an explicit scope outside of an HTTP request.
// EnsureDeleted() drops the SQLite file — gives us a fresh seed every run.
// EnsureCreated() rebuilds the schema + runs HasData() seeds from BankDbContext.
// Production would use migrations (dotnet ef database update) instead.
// Why delete first? SQLite persists to a file, so old data survives restarts.
// For a learning project, fresh data every run avoids confusion.
//
// Note: in integration tests the WebApplicationFactory replaces the DbContext
// registration with InMemory before this runs, so EnsureDeleted/EnsureCreated
// here will operate on the test factory's in-memory database — which is what
// we want. The factory's own seed (Alice/Bob/Carol) runs after this scope.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BankDbContext>();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
}

// --- DI registration diagnostics (dev only) ---
// Prints all our custom registrations with their lifetimes.
// Useful for verifying: "Did AddMediatR actually register my handlers?"
// Remove before production — leaks internal architecture info.
// Note: no scope needed — descriptors come from builder.Services directly,
// not from a resolved service. Keeping this allocation-free.
if (app.Environment.IsDevelopment())
{
    var descriptors = builder.Services
        .Where(s => s.ServiceType.Namespace?.StartsWith("PesaCore.Api") == true
                 || s.ServiceType.FullName?.Contains("DbContext") == true
                 || s.ServiceType.FullName?.Contains("Mapper") == true
                 || s.ServiceType.FullName?.Contains("Mediator") == true
                 || s.ServiceType.FullName?.Contains("Validator") == true
                 || s.ServiceType.FullName?.Contains("Behavior") == true)
        .OrderBy(s => s.Lifetime);

    Log.Information("=== DI REGISTRATIONS ===");
    foreach (var d in descriptors)
    {
        Log.Information("  {Lifetime,-12} {ServiceType}", d.Lifetime, d.ServiceType.Name);
    }
}

// ===== RUN — wrapped in try/catch/finally =====
// Only Run() is wrapped, not Build(). Startup failures from Build() or middleware
// setup propagate as exceptions out of Build()/Use*() calls — those crash the
// process loud, which is the correct behaviour: a misconfigured banking app must
// fail fast at startup, not silently route traffic with broken DI.
//
// At Run() time:
//   - HostAbortedException: WebApplicationFactory's stop signal in tests. Filtered
//     out so we don't log it as Fatal (it's expected).
//   - Anything else: log Fatal with full exception, then re-throw so the process
//     exits with a non-zero code (orchestrators see the crash and won't roll forward).
try
{
    app.Run();
}
catch (Exception ex) when (ex is not Microsoft.Extensions.Hosting.HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    // CloseAndFlush: ensures all buffered log events are written before the process exits.
    // Serilog buffers writes for performance — without this, the last few log lines
    // (including the Fatal above) might be lost on process termination.
    Log.CloseAndFlush();
}

// Scaffold record — kept for reference. This is a positional record (C# 9):
// constructor params become init-only properties with value equality.
// Same pattern as AccountDto but with a computed property (TemperatureF).
record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

// Re-declare Program as public so WebApplicationFactory<Program> in
// PesaCore.Api.Tests can reference the entry point. Without this, top-level
// statements generate an internal Program class that's invisible to a
// different assembly. See docs/paved_road.md standard #16.
public partial class Program { }
