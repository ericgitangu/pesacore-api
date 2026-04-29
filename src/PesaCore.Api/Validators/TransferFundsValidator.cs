using FluentValidation;
using PesaCore.Api.Features;

namespace PesaCore.Api.Validators;

// ===== FLUENT VALIDATION — business rules separated from data shape =====
//
// Why not use [Required], [Range] data annotations on the DTO/Command?
//   1. Annotations mix validation with data shape — the DTO becomes cluttered
//   2. Complex rules (cross-field, conditional, async DB lookups) don't fit attributes
//   3. Banking validation often has business context — "amounts over 1M need dual-approval"
//      is a business rule, not a data constraint
//   4. Testability — validators are plain classes, easy to unit test in isolation
//
// FluentValidation separates concerns:
//   - DTOs/Commands hold SHAPE (what fields exist, what types they are)
//   - Validators hold RULES (what values are acceptable, what combinations are valid)
//   - Handlers hold BEHAVIOR (what to do with valid data)
//
// Auto-validation: because we called AddFluentValidationAutoValidation() in Program.cs,
// ASP.NET Core's model binding pipeline runs this validator BEFORE the action executes.
// If validation fails, the framework returns 400 + validation errors automatically.
// The handler never sees invalid data.
//
// Java equivalent: javax.validation (Bean Validation) with @Valid + custom ConstraintValidator
// Python equivalent: Pydantic validators (@field_validator, @model_validator) or Marshmallow
public class TransferFundsValidator : AbstractValidator<TransferFundsCommand>
{
    public TransferFundsValidator()
    {
        // --- Field-level rules ---

        // NotEmpty: rejects null, "", and whitespace. Different from NotNull (allows "").
        // Matches: regex validation — account format must be EQB followed by 3+ digits.
        // In real Equity banking, account numbers have a specific format per product type
        // (savings, current, fixed deposit) — the regex would be more complex.
        RuleFor(x => x.FromAccount)
            .NotEmpty().WithMessage("Source account is required")
            .Matches(@"^EQB\d{3,}$").WithMessage("Account number must match EQBxxx format");

        RuleFor(x => x.ToAccount)
            .NotEmpty().WithMessage("Destination account is required")
            .Matches(@"^EQB\d{3,}$").WithMessage("Account number must match EQBxxx format");

        // --- Cross-field rule ---
        // RuleFor(x => x) validates the entire object, not a single property.
        // Must() takes a predicate — return true if valid, false if not.
        // This catches a common user error: accidentally transferring to the same account.
        // In banking, this is also a fraud signal — circular transfers are suspicious.
        RuleFor(x => x)
            .Must(x => x.FromAccount != x.ToAccount)
            .WithMessage("Cannot transfer to the same account");

        // --- Business rule: amount constraints ---
        // GreaterThan(0): no zero or negative transfers.
        // LessThanOrEqualTo(1_000_000m): models a real banking constraint —
        // large transfers require a different approval path (maker-checker pattern,
        // dual authorization, compliance review). The 1M threshold is illustrative;
        // real thresholds are configured per product and regulatory regime.
        // CBK has specific reporting requirements for transactions over KES 1M
        // (suspicious transaction reports under the Proceeds of Crime and Anti-Money Laundering Act).
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be positive")
            .LessThanOrEqualTo(1_000_000m).WithMessage(
                "Amounts over 1M require dual-approval endpoint (not implemented)");
    }
}
