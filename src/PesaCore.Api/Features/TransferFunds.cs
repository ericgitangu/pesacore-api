using MediatR;
using Microsoft.EntityFrameworkCore;
using PesaCore.Api.Data;

namespace PesaCore.Api.Features;

// ===== CQRS COMMAND — changes state, has side effects =====
//
// Commands are the write side of CQRS. They mutate state and return a result
// indicating success/failure. Key differences from queries:
//   - NOT safe to retry blindly (double-debit risk)
//   - Need validation BEFORE execution (FluentValidation in Step 8)
//   - Need idempotency in production (idempotency key to prevent duplicate transfers)
//   - May publish domain events (TransferCompleted, InsufficientFunds)
//
// This is a fund transfer — the canonical banking operation.
// Real production version would add:
//   - Transaction wrapping (BEGIN/COMMIT/ROLLBACK)
//   - Idempotency key check (prevent double-submit)
//   - Outbox pattern for event publishing (ensure event + state change are atomic)
//   - Audit trail (who, when, what, from where)
//   - Regulatory checks (AML/KYC screening, CBK daily transfer limits)

// The Command — what the caller wants to happen.
// [FromBody] in the controller deserializes JSON into this record.
// Record properties map to JSON camelCase by default (ASP.NET Core convention):
//   { "fromAccount": "EQB001", "toAccount": "EQB002", "amount": 500 }
public record TransferFundsCommand(
    string FromAccount,
    string ToAccount,
    decimal Amount) : IRequest<TransferResult>;

// The Result — success/failure signal + message + updated state.
// Returning a result (not void) is a deliberate choice:
//   - Caller can show the new balance without a second query
//   - Error messages are structured, not exception-based
//   - No ambiguity about what happened
public record TransferResult(bool Success, string Message, decimal? NewBalance);

public class TransferFundsHandler : IRequestHandler<TransferFundsCommand, TransferResult>
{
    private readonly BankDbContext _db;

    public TransferFundsHandler(BankDbContext db)
    {
        _db = db;
    }

    public async Task<TransferResult> Handle(
        TransferFundsCommand request,
        CancellationToken cancellationToken)
    {
        // Load both accounts — FirstOrDefaultAsync returns null if not found.
        // In production you'd use a transaction (IsolationLevel.Serializable for money)
        // to prevent concurrent modifications between the read and the write.
        var from = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.FromAccount, cancellationToken);
        var to = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.ToAccount, cancellationToken);

        if (from == null || to == null)
            return new TransferResult(false, "Account not found", null);

        // Business rule: can't overdraft. Real banking has more complex rules —
        // overdraft limits, hold amounts, pending transactions reducing available balance.
        if (from.Balance < request.Amount)
            return new TransferResult(false, "Insufficient funds", from.Balance);

        // Mutation — EF Core's change tracker detects these property changes.
        // SaveChangesAsync generates UPDATE statements for both accounts.
        // With InMemoryDatabase this is instant; with SQL Server it'd be in a transaction.
        from.Balance -= request.Amount;
        to.Balance += request.Amount;

        // SaveChangesAsync: flushes all tracked changes to the database in one batch.
        // If this throws, neither account is modified (atomic at the EF level).
        // CancellationToken ensures we don't commit if the client has disconnected.
        await _db.SaveChangesAsync(cancellationToken);

        return new TransferResult(true, "Transfer successful", from.Balance);
    }
}
