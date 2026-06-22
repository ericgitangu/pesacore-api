using FluentAssertions;
using FluentValidation.TestHelper;
using PesaCore.Features;
using PesaCore.Validators;

namespace PesaCore.Tests.Validators;

// ===== VALIDATOR TESTS — rules for opening an account =====
// Mirrors TransferFundsValidatorTests' style: pure rule verification via
// FluentValidation's TestHelper, no DB, no mocks.
public class CreateAccountValidatorTests
{
    private readonly CreateAccountValidator _validator = new();

    [Fact]
    public async Task ValidCommand_PassesAllRules()
    {
        var command = new CreateAccountCommand("Dana", 2_500m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task ZeroOpeningBalance_PassesValidation()
    {
        var command = new CreateAccountCommand("Dana", 0m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveValidationErrorFor(x => x.OpeningBalance);
    }

    [Theory]
    [InlineData("")]      // empty
    [InlineData("   ")]   // whitespace -> NotEmpty
    [InlineData("D")]     // too short (min 2)
    public async Task InvalidHolderName_FailsValidation(string holderName)
    {
        var command = new CreateAccountCommand(holderName, 100m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.HolderName);
    }

    [Fact]
    public async Task HolderNameOver100Chars_FailsValidation()
    {
        var command = new CreateAccountCommand(new string('A', 101), 100m);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.HolderName);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public async Task NegativeOpeningBalance_FailsValidation(decimal opening)
    {
        var command = new CreateAccountCommand("Dana", opening);

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.OpeningBalance);
    }
}
