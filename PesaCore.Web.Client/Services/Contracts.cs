namespace PesaCore.Web.Client.Services;

// ===== Wire contracts — mirror the PesaCore server records exactly =====
// JSON is camelCase on the wire (ASP.NET Core default). System.Text.Json
// deserialization via GetFromJsonAsync is case-insensitive, so PascalCase
// properties bind to camelCase JSON without attributes.

// Mirrors PesaCore.Dtos.AccountDto:
//   record AccountDto(string AccountNumber, string HolderName, decimal Balance, int TransactionCount)
// Source: GET /Accounts/dto/linq
public sealed record AccountDto(
    string AccountNumber,
    string HolderName,
    decimal Balance,
    int TransactionCount);

// Mirrors PesaCore.Features.AccountBalanceResult:
//   record AccountBalanceResult(string AccountNumber, string HolderName, decimal Balance)
// Source: GET /Cqrs/balance/{accountNumber}
public sealed record AccountBalance(
    string AccountNumber,
    string HolderName,
    decimal Balance);

// Mirrors PesaCore.Features.TransferFundsCommand:
//   record TransferFundsCommand(string FromAccount, string ToAccount, decimal Amount)
// Target: POST /Cqrs/transfer
public sealed record TransferRequest(
    string FromAccount,
    string ToAccount,
    decimal Amount);

// Mirrors PesaCore.Features.TransferResult:
//   record TransferResult(bool Success, string Message, decimal? NewBalance)
public sealed record TransferResult(
    bool Success,
    string Message,
    decimal? NewBalance);

// Mirrors PesaCore.Features.CreateAccountCommand:
//   record CreateAccountCommand(string HolderName, decimal OpeningBalance)
// Target: POST /Cqrs/accounts
public sealed record CreateAccountRequest(
    string HolderName,
    decimal OpeningBalance);

// Mirrors PesaCore.Features.CreateAccountResult:
//   record CreateAccountResult(bool Success, string Message, string? AccountNumber,
//                              string? HolderName, decimal? Balance)
public sealed record CreateAccountResult(
    bool Success,
    string Message,
    string? AccountNumber,
    string? HolderName,
    decimal? Balance);
