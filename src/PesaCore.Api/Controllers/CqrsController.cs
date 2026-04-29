using Microsoft.AspNetCore.Mvc;
using MediatR;
using PesaCore.Api.Features;

namespace PesaCore.Api.Controllers;

// ===== CQRS CONTROLLER — thin dispatcher, no business logic =====
//
// This controller does exactly three things:
//   1. Receives HTTP request → deserializes into a MediatR message
//   2. Dispatches via IMediator.Send() → handler runs
//   3. Translates the handler result → HTTP response
//
// No database access. No business rules. No validation logic.
// All of that lives in handlers (Step 5) and validators (Step 8).
//
// Why this shape?
//   - Single Responsibility: controller handles HTTP, handler handles business logic
//   - Testability: handler is testable without HTTP; controller is testable without DB
//   - Cross-cutting: MediatR pipeline behaviors (logging, validation, caching) wrap
//     every Send() call without modifying controllers or handlers
//   - Scalability: if you split to separate read/write services, controllers don't change
//
// Java equivalent: thin @RestController that delegates to a CommandBus/QueryBus
// Python equivalent: FastAPI route that calls a service/use-case layer

[ApiController]
[Route("[controller]")]
public class CqrsController : ControllerBase
{
    // IMediator — MediatR's dispatch interface.
    // Send() routes a message to its matching IRequestHandler<TRequest, TResult>.
    // The routing is by type — MediatR looks up the handler registered for that message type.
    // Registered as Transient by default, but the underlying ServiceFactory is Scoped.
    private readonly IMediator _mediator;

    public CqrsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    // QUERY — read-only, safe to cache, safe to retry.
    // GET is the correct HTTP verb for queries (idempotent, cacheable).
    // Returns 200 + data if found, 404 if not.
    [HttpGet("balance/{accountNumber}")]
    public async Task<IActionResult> GetBalance(string accountNumber)
    {
        var result = await _mediator.Send(new GetAccountBalanceQuery(accountNumber));
        return result == null ? NotFound() : Ok(result);
    }

    // COMMAND — mutates state, NOT safe to retry without idempotency.
    // POST is the correct HTTP verb for commands (non-idempotent by default).
    // [FromBody] deserializes the JSON request body into TransferFundsCommand.
    // Returns 200 if successful, 400 if business rule violation.
    //
    // Idempotency is enforced by IdempotencyBehavior in the MediatR pipeline —
    // every POST/PUT/PATCH must include X-Idempotency-Key header (UUID v4).
    // Missing key → 400; duplicate key → cached response (no re-execution).
    // See: Behaviors/IdempotencyBehavior.cs.
    //
    // In production you'd also add:
    //   - 201 Created with Location header for audit trail
    //   - Rate limiting per account to prevent abuse
    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferFundsCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
