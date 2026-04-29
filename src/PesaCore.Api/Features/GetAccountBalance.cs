using MediatR;
using Microsoft.EntityFrameworkCore;
using PesaCore.Api.Data;

namespace PesaCore.Api.Features;

// ===== CQRS QUERY — reads data, no side effects =====
//
// CQRS (Command Query Responsibility Segregation) splits reads and writes:
//   Query  = "give me data" (no mutation, safe to cache, safe to retry)
//   Command = "change something" (mutation, needs idempotency, needs validation)
//
// MediatR implements this via IRequest<T> (the message) + IRequestHandler<TRequest, TResult> (the handler).
// The controller sends the message via IMediator.Send() — it doesn't know which handler runs.
// This decoupling means:
//   1. Controllers stay thin — just HTTP-to-message translation
//   2. Handlers own business logic — testable without HTTP
//   3. Cross-cutting concerns (logging, validation, caching) plug in via MediatR pipeline behaviors
//   4. Evolution path — at scale, queries can read from a denormalized view or different DB
//      without changing the controller or the query contract
//
// Java equivalent: Axon Framework's @QueryHandler or manual CQRS with Spring ApplicationEventPublisher
// Python equivalent: no direct equivalent — you'd build this with a mediator pattern or use a library like pymediatr

// The Query — a record implementing IRequest<T>.
// T is the return type. The "?" means this query might return null (account not found).
// Records are ideal for messages — immutable, value equality, concise.
public record GetAccountBalanceQuery(string AccountNumber) : IRequest<AccountBalanceResult?>;

// The Result — what the handler returns. Also a record.
// This is NOT the same as the entity — it's a purpose-built response shape.
// The query handler decides what data to include; the entity has more.
public record AccountBalanceResult(string AccountNumber, string HolderName, decimal Balance);

// The Handler — one handler per query. Implements IRequestHandler<TQuery, TResult>.
// MediatR discovers this via assembly scanning (RegisterServicesFromAssembly in Program.cs).
// Handlers default to Transient lifetime — a new instance per Send() call.
// This is fine because they're lightweight and stateless.
public class GetAccountBalanceHandler : IRequestHandler<GetAccountBalanceQuery, AccountBalanceResult?>
{
    private readonly BankDbContext _db;

    // Constructor injection — same DI pattern as controllers.
    // DbContext is Scoped, handler is Transient — both live within the HTTP request scope.
    public GetAccountBalanceHandler(BankDbContext db)
    {
        _db = db;
    }

    public async Task<AccountBalanceResult?> Handle(
        GetAccountBalanceQuery request,
        CancellationToken cancellationToken)
    {
        // Projection query — only fetches the 3 fields we need, not the full entity.
        // FirstOrDefaultAsync returns null if no match — the "?" on the return type.
        // CancellationToken: propagated from the HTTP request. If the client disconnects,
        // the token fires and EF cancels the DB query. Without this, orphaned queries
        // consume DB connections for nothing. In banking at scale, this matters.
        var account = await _db.Accounts
            .Where(a => a.AccountNumber == request.AccountNumber)
            .Select(a => new AccountBalanceResult(a.AccountNumber, a.HolderName, a.Balance))
            .FirstOrDefaultAsync(cancellationToken);

        return account;
    }
}
