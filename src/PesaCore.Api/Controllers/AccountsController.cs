using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using PesaCore.Api.Data;
using PesaCore.Api.Dtos;
using PesaCore.Api.Services;

namespace PesaCore.Api.Controllers;

// [ApiController] adds behavior you'd otherwise write by hand:
//   1. Automatic model validation — if ModelState is invalid, returns 400 before your code runs
//   2. Binding source inference — [FromBody] for complex types, [FromRoute]/[FromQuery] for simple
//   3. Problem Details responses — errors return RFC 7807 JSON, not plain text
// Without it, you'd need explicit ModelState.IsValid checks in every action.
// Java equivalent: think @RestController = @Controller + @ResponseBody — same idea, bundled defaults.
[ApiController]

// [Route("[controller]")] — convention-based token replacement.
// "[controller]" is replaced at startup with the class name minus "Controller" suffix:
//   AccountsController → /accounts
// Alternative: [Route("api/v1/accounts")] for explicit versioned routes.
// In banking APIs, explicit versioning is common — /api/v1/accounts, /api/v2/accounts —
// because breaking changes to financial endpoints need careful migration windows.
[Route("[controller]")]
public class AccountsController : ControllerBase
{
    // private: no outside access — encapsulation.
    // readonly: can only be assigned in constructor — prevents accidental reassignment.
    // Together they guarantee _db is set once at construction and never swapped.
    // Java equivalent: private final BankDbContext db;
    // Python equivalent: conceptually self._db set in __init__ (but Python has no compile-time enforcement).
    private readonly BankDbContext _db;

    // IMapper — AutoMapper's runtime mapping interface, registered as Singleton in DI.
    // Singleton because MapperConfiguration (the compiled mapping rules) is immutable,
    // thread-safe, and expensive to build — exactly the profile of a singleton service.
    // Java equivalent: injecting a ModelMapper or MapStruct-generated mapper interface.
    private readonly IMapper _mapper;
    private readonly IFinacleClient _finacle;

    // Constructor injection — ASP.NET Core's DI container resolves all three automatically.
    // BankDbContext: Scoped (one per HTTP request) — own change tracker and connection.
    // IMapper: Singleton (one for app lifetime) — shared, stateless, thread-safe.
    // IFinacleClient: Singleton — simulated core banking client.
    // When the request ends, the scope disposes the context (closing connections).
    // Java/Spring equivalent: @Autowired constructor injection (or just a constructor in Spring 4.3+).
    public AccountsController(BankDbContext db, IMapper mapper, IFinacleClient finacle)
    {
        _db = db;
        _mapper = mapper;
        _finacle = finacle;
    }

    // --- Return type: IActionResult ---
    // IActionResult is the polymorphic return type for controller actions.
    // It lets a single method return different HTTP status codes:
    //   return Ok(data)        → 200 + JSON body
    //   return NotFound()      → 404
    //   return BadRequest(msg) → 400
    //   return CreatedAtAction(...) → 201 + Location header
    //
    // Alternative: ActionResult<T> — same flexibility but also declares the success type,
    // so OpenAPI/Swagger can generate typed response schemas automatically.
    //   public ActionResult<List<AccountDto>> Get() → Swagger knows the 200 shape
    //   public IActionResult Get()                  → Swagger only knows it's "some object"
    //
    // Third option: just return the object (e.g., public List<AccountDto> Get()).
    // Simplest, but locks you into 200 — can't return 404 without throwing exceptions.
    //
    // Rule of thumb: ActionResult<T> for production APIs (typed + flexible),
    // IActionResult for demos/learning where you don't need Swagger precision.

    // N+1 PROBLEM — DO NOT SHIP THIS CODE
    // ToList() executes the query immediately, loading all Account entities into memory.
    // Then a.Transactions.Count triggers lazy loading — EF fires a SEPARATE query per account.
    // 3 accounts = 1 + 3 = 4 queries. 10,000 accounts = 10,001 queries.
    // In banking at scale (Equity has 19M+ accounts), this would melt the database.
    //
    // Generated SQL (N+1 — multiple round-trips):
    //   Query 1:  SELECT "a"."Id", "a"."AccountNumber", "a"."Balance", "a"."HolderName"
    //             FROM "Accounts" AS "a"
    //   Query 2:  SELECT "t"."Id", "t"."AccountId", "t"."Amount", "t"."Description", "t"."Timestamp"
    //             FROM "Transactions" AS "t" WHERE "t"."AccountId" = @accountId  -- repeated per account!
    [HttpGet("bad")]
    public IActionResult GetAllAccountsBad()
    {
        var accounts = _db.Accounts.ToList();
        var result = accounts.Select(a => new
        {
            a.AccountNumber,
            a.HolderName,
            TransactionCount = a.Transactions.Count
        }).ToList();
        return Ok(result);
    }

    // EAGER LOAD — ONE QUERY WITH JOIN
    // .Include() tells EF to LEFT JOIN Transactions in the same query.
    // One round-trip, all data loaded. But: loads FULL Transaction entities into memory
    // even though we only need the count. For wide tables with many columns, this is wasteful.
    //
    // Generated SQL (one query, but fetches ALL columns from BOTH tables):
    //   SELECT "a"."Id", "a"."AccountNumber", "a"."Balance", "a"."HolderName",
    //          "t"."Id", "t"."AccountId", "t"."Amount", "t"."Description", "t"."Timestamp"
    //   FROM "Accounts" AS "a"
    //   LEFT JOIN "Transactions" AS "t" ON "a"."Id" = "t"."AccountId"
    //   ORDER BY "a"."Id"
    [HttpGet("good")]
    public IActionResult GetAllAccountsGood()
    {
        var accounts = _db.Accounts
            .Include(a => a.Transactions)
            .ToList();
        var result = accounts.Select(a => new
        {
            a.AccountNumber,
            a.HolderName,
            TransactionCount = a.Transactions.Count
        }).ToList();
        return Ok(result);
    }

    // PROJECTION — BEST: ONLY FETCH WHAT YOU NEED
    // .Select() before .ToList() means the projection runs in SQL, not in C#.
    // No Transaction entities are loaded. No tracking overhead. Minimal data over the wire.
    // This is the pattern for banking hot paths — account listings, balance checks, dashboards.
    //
    // Generated SQL (one query, minimal columns, COUNT subquery):
    //   SELECT "a"."AccountNumber", "a"."HolderName",
    //          (SELECT COUNT(*) FROM "Transactions" AS "t" WHERE "a"."Id" = "t"."AccountId")
    //   FROM "Accounts" AS "a"
    [HttpGet("best")]
    public IActionResult GetAllAccountsBest()
    {
        var result = _db.Accounts
            .Select(a => new
            {
                a.AccountNumber,
                a.HolderName,
                TransactionCount = a.Transactions.Count()
            })
            .ToList();
        return Ok(result);
    }

    // ===== STEP 3: DTO MAPPING STRATEGIES =====
    // Three ways to get from Entity → DTO, each with different SQL and memory trade-offs.
    // Same result, different cost. Interview answer: "It depends on the query profile."

    // STRATEGY 1: AutoMapper .Map() — convenient, but loads full entities first
    // Flow: DB → full entities into memory → AutoMapper transforms in C# → DTO
    // When to use: small result sets, complex mapping logic, prototyping.
    // When NOT to use: hot paths, large tables, banking dashboards with 10K+ rows.
    // Java equivalent: loading JPA entities then calling modelMapper.map(entity, Dto.class)
    //
    // Generated SQL (same as /good — LEFT JOIN, all columns, all rows):
    //   SELECT "a"."Id", "a"."AccountNumber", "a"."Balance", "a"."HolderName",
    //          "t"."Id", "t"."AccountId", "t"."Amount", "t"."Description", "t"."Timestamp"
    //   FROM "Accounts" AS "a"
    //   LEFT JOIN "Transactions" AS "t" ON "a"."Id" = "t"."AccountId"
    //   ORDER BY "a"."Id"
    [HttpGet("dto/automapper")]
    public IActionResult GetAllAccountsAutoMapper()
    {
        var accounts = _db.Accounts
            .Include(a => a.Transactions)
            .ToList();
        var dtos = _mapper.Map<List<AccountDto>>(accounts);
        return Ok(dtos);
    }

    // STRATEGY 2: AutoMapper .ProjectTo() — pushes the projection into SQL
    // Flow: DB → SQL with only needed columns → DTO (no entities tracked)
    // This is the sweet spot — declarative mapping (Profile-based) with projection efficiency.
    // No Include() needed — AutoMapper reads the Profile and tells EF what columns to SELECT.
    // ConfigurationProvider is the compiled MapperConfiguration that holds all Profile rules.
    // Java equivalent: MapStruct + JPQL projection — compile-time generated mapper + DB projection.
    //
    // Generated SQL (projection — only the columns the DTO needs):
    //   SELECT "a"."AccountNumber", "a"."HolderName", "a"."Balance",
    //          (SELECT COUNT(*) FROM "Transactions" AS "t" WHERE "a"."Id" = "t"."AccountId")
    //   FROM "Accounts" AS "a"
    [HttpGet("dto/projectto")]
    public IActionResult GetAllAccountsProjectTo()
    {
        var dtos = _db.Accounts
            .ProjectTo<AccountDto>(_mapper.ConfigurationProvider)
            .ToList();
        return Ok(dtos);
    }

    // STRATEGY 3: LINQ Select — explicit, no magic, full control
    // Flow: same SQL as ProjectTo, but you write the projection by hand.
    // No AutoMapper dependency. You see exactly what SQL will run.
    // Trade-off: more verbose, harder to maintain when DTOs have 20+ fields.
    // In banking: preferred for critical financial queries where you want zero ambiguity
    // about what data crosses the wire. Auditors can read LINQ; AutoMapper profiles are opaque.
    //
    // Generated SQL (identical to ProjectTo — same projection, same efficiency):
    //   SELECT "a"."AccountNumber", "a"."HolderName", "a"."Balance",
    //          (SELECT COUNT(*) FROM "Transactions" AS "t" WHERE "a"."Id" = "t"."AccountId")
    //   FROM "Accounts" AS "a"
    [HttpGet("dto/linq")]
    public IActionResult GetAllAccountsLinq()
    {
        var dtos = _db.Accounts
            .Select(a => new AccountDto(
                a.AccountNumber,
                a.HolderName,
                a.Balance,
                a.Transactions.Count))
            .ToList();
        return Ok(dtos);
    }

    // ===== STEP 4: POLLY RESILIENCE PATTERNS =====
    // Three tiers of resilience for calling a flaky external dependency (Finacle CBS).
    // Each tier adds protection. Interview answer: "Retry alone is naive — you need
    // circuit breaker to stop hammering a system that's already down."

    // TIER 1: NAIVE — no resilience. Direct call, fails ~50% of the time.
    // This is what most junior code looks like — call and pray.
    // In banking: unacceptable. A flaky Finacle call that fails silently or crashes
    // the request means a customer can't see their balance. That's a P1.
    [HttpGet("finacle/naive/{accountNumber}")]
    public async Task<IActionResult> GetStatusNaive(string accountNumber)
    {
        var status = await _finacle.GetAccountStatusAsync(accountNumber);
        return Ok(new { accountNumber, status });
    }

    // TIER 2: RETRY — tries 3 times with exponential backoff.
    // Exponential backoff: 200ms, 400ms, 800ms (100 * 2^attempt).
    // Why exponential? Constant retry hammers the dependency at the worst time.
    // Exponential backs off geometrically — gives the dependency breathing room.
    // onRetry callback logs each attempt — watch the terminal to see it fire.
    // Java equivalent: Spring Retry @Retryable(maxAttempts=3, backoff=@Backoff(delay=200, multiplier=2))
    // Python equivalent: tenacity.retry(wait=wait_exponential(), stop=stop_after_attempt(3))
    //
    // Problem with retry alone: if Finacle is truly DOWN (not just flaky),
    // retries just add latency — 3 retries × backoff = ~1.4s wasted per request.
    // Multiply by 1000 concurrent requests = thread pool exhaustion.
    [HttpGet("finacle/retry/{accountNumber}")]
    public async Task<IActionResult> GetStatusWithRetry(string accountNumber)
    {
        var retryPolicy = Policy
            .Handle<InvalidOperationException>()    // Only retry this exception type
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                onRetry: (exception, timespan, attempt, context) =>
                {
                    Console.WriteLine(
                        $"  Retry #{attempt} after {timespan.TotalMilliseconds}ms " +
                        $"due to: {exception.Message}");
                });

        var status = await retryPolicy.ExecuteAsync(
            () => _finacle.GetAccountStatusAsync(accountNumber));

        return Ok(new { accountNumber, status });
    }

    // TIER 3: RETRY + CIRCUIT BREAKER — the production pattern.
    //
    // Circuit breaker is a STATE MACHINE with three states:
    //   CLOSED  → normal operation, requests flow through
    //   OPEN    → after N failures, ALL requests fail-fast with BrokenCircuitException
    //   HALF-OPEN → after the break duration, ONE probe request is allowed through
    //     - If probe succeeds → CLOSED (resume normal)
    //     - If probe fails → OPEN again (extend the break)
    //
    // Why static? Circuit state must persist across HTTP requests.
    // If it were instance-level, each request would get a fresh circuit (useless).
    // Controllers are Transient in ASP.NET Core — new instance per request.
    // Static fields survive the class lifetime = shared across all requests.
    // Java equivalent: Resilience4j CircuitBreaker (static or Spring-managed singleton)
    //
    // The Wrap: circuit breaker wraps retry. Order matters:
    //   Request → CircuitBreaker checks state → if CLOSED, Retry executes → call Finacle
    //   If circuit is OPEN → BrokenCircuitException immediately (no retry, no call)
    //   This is the fail-fast behavior — don't waste time retrying a dead system.
    private static readonly AsyncCircuitBreakerPolicy _circuitBreaker = Policy
        .Handle<InvalidOperationException>()
        .CircuitBreakerAsync(
            exceptionsAllowedBeforeBreaking: 3,          // Trip after 3 failures
            durationOfBreak: TimeSpan.FromSeconds(10),   // Stay open for 10s
            onBreak: (ex, duration) =>
                Console.WriteLine($"  CIRCUIT OPEN for {duration.TotalSeconds}s: {ex.Message}"),
            onReset: () =>
                Console.WriteLine("  CIRCUIT CLOSED — operations resuming"),
            onHalfOpen: () =>
                Console.WriteLine("  CIRCUIT HALF-OPEN — probing with single request"));

    private static readonly AsyncRetryPolicy _retryForCircuit = Policy
        .Handle<InvalidOperationException>()
        .WaitAndRetryAsync(
            retryCount: 2,
            sleepDurationProvider: attempt =>
                TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)));

    [HttpGet("finacle/resilient/{accountNumber}")]
    public async Task<IActionResult> GetStatusResilient(string accountNumber)
    {
        var resiliencePolicy = _circuitBreaker.WrapAsync(_retryForCircuit);

        try
        {
            var status = await resiliencePolicy.ExecuteAsync(
                () => _finacle.GetAccountStatusAsync(accountNumber));
            return Ok(new { accountNumber, status });
        }
        catch (BrokenCircuitException)
        {
            return StatusCode(503, new
            {
                error = "Finacle circuit is open — failing fast. Retry in a few seconds."
            });
        }
    }
}
