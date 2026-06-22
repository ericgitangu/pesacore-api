using FluentValidation;
using PesaCore.Features;

namespace PesaCore.Validators;

// ===== FLUENT VALIDATION — rules for opening an account =====
//
// Same separation-of-concerns as TransferFundsValidator: the command holds SHAPE,
// this holds RULES, the handler holds BEHAVIOR. Runs automatically via
// AddFluentValidationAutoValidation before CreateAccountHandler ever sees the request,
// so the handler can assume HolderName is present and OpeningBalance is non-negative.
public class CreateAccountValidator : AbstractValidator<CreateAccountCommand>
{
    public CreateAccountValidator()
    {
        // NotEmpty rejects null/""/whitespace; Length bounds it. 2 chars is the realistic
        // floor for a holder name; 100 caps storage + matches a typical DB column width.
        RuleFor(x => x.HolderName)
            .NotEmpty().WithMessage("Holder name is required")
            .Length(2, 100).WithMessage("Holder name must be 2–100 characters");

        // Opening balance may be zero (open empty, fund later) but never negative —
        // a negative opening balance is a phantom liability, not a deposit.
        RuleFor(x => x.OpeningBalance)
            .GreaterThanOrEqualTo(0m).WithMessage("Opening balance cannot be negative");
    }
}
