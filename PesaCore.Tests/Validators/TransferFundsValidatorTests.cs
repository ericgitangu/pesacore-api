using FluentAssertions;
using FluentValidation.TestHelper;
using PesaCore.Features;
using PesaCore.Validators;

namespace PesaCore.Tests.Validators;

// ===== VALIDATOR TESTS — the simplest unit tests in the project =====
//
// FluentValidation's TestHelper provides TestValidateAsync which returns a
// TestValidationResult with ShouldHaveValidationErrorFor / ShouldNotHaveValidationErrorFor.
// This is the recommended approach — no mocking, no DB, pure rule verification.
//
// Banking rationale: validation is the first line of defense. If these tests break,
// bad data reaches the handler. At the bank's scale, a missing validation rule on
// transfer amounts could mean regulatory violations (KES 1M+ without dual-approval).
//
// Java equivalent: testing @Valid constraints with Hibernate Validator's test harness.
// Python equivalent: testing Pydantic model validation with pytest.raises(ValidationError).
public class TransferFundsValidatorTests
{
    private readonly TransferFundsValidator _validator = new();

    // --- Happy path ---

    [Fact]
    public async Task ValidCommand_PassesAllRules()
    {
        var command = new TransferFundsCommand("EQB001", "EQB002", 500m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // --- Account format rules ---

    [Theory]
    [InlineData("")]           // empty
    [InlineData("   ")]        // whitespace
    [InlineData("ABC001")]     // wrong prefix
    [InlineData("EQB")]        // missing digits
    [InlineData("EQB12")]      // too few digits (need 3+)
    [InlineData("eqb001")]     // lowercase — regex is case-sensitive
    public async Task InvalidFromAccount_FailsValidation(string fromAccount)
    {
        var command = new TransferFundsCommand(fromAccount, "EQB002", 500m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.FromAccount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("INVALID")]
    public async Task InvalidToAccount_FailsValidation(string toAccount)
    {
        var command = new TransferFundsCommand("EQB001", toAccount, 500m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.ToAccount);
    }

    // --- Cross-field rule: circular transfer detection ---

    [Fact]
    public async Task SameSourceAndDestination_FailsValidation()
    {
        // Circular transfer is a fraud signal in banking.
        // Same-account transfers can mask money laundering patterns.
        var command = new TransferFundsCommand("EQB001", "EQB001", 500m);

        var result = await _validator.TestValidateAsync(command);

        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("Cannot transfer to the same account"));
    }

    // --- Amount boundary rules ---

    [Theory]
    [InlineData(0)]        // zero
    [InlineData(-100)]     // negative
    [InlineData(-0.01)]    // tiny negative
    public async Task NonPositiveAmount_FailsValidation(decimal amount)
    {
        var command = new TransferFundsCommand("EQB001", "EQB002", amount);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public async Task AmountExceedsDualApprovalThreshold_FailsValidation()
    {
        // KES 1M+ requires dual-approval path — CBK reporting threshold.
        var command = new TransferFundsCommand("EQB001", "EQB002", 1_000_001m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Amount)
            .WithErrorMessage("Amounts over 1M require dual-approval endpoint (not implemented)");
    }

    [Fact]
    public async Task AmountAtExactThreshold_PassesValidation()
    {
        // Boundary test: exactly 1M should pass (LessThanOrEqualTo).
        var command = new TransferFundsCommand("EQB001", "EQB002", 1_000_000m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public async Task SmallestValidAmount_PassesValidation()
    {
        // Minimum valid transfer: 0.01 (one cent equivalent).
        var command = new TransferFundsCommand("EQB001", "EQB002", 0.01m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
