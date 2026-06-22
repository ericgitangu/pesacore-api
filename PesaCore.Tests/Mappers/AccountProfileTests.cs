using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PesaCore.Dtos;
using PesaCore.Mappers;
using PesaCore.Models;

namespace PesaCore.Tests.Mappers;

// ===== AUTOMAPPER PROFILE TESTS — mapping correctness verification =====
//
// AutoMapper is runtime magic — it resolves mappings by convention (matching names)
// and explicit config (ForCtorParam). If a property is renamed or a mapping breaks,
// you find out at runtime, not compile-time. These tests catch that.
//
// AssertConfigurationIsValid() is AutoMapper's built-in sanity check — it verifies
// every mapped property has a source. If we add a field to AccountDto and forget
// to update the profile, this test fails.
//
// Java equivalent: MapStruct generates code at compile time so mapping errors
// are caught earlier. ModelMapper needs similar runtime verification tests.
public class AccountProfileTests
{
    private readonly IMapper _mapper;

    public AccountProfileTests()
    {
        // AutoMapper 16 uses AddMaps for assembly scanning (same pattern as Program.cs).
        var config = new MapperConfiguration(cfg => cfg.AddMaps(typeof(AccountProfile).Assembly), NullLoggerFactory.Instance);
        _mapper = config.CreateMapper();
    }

    [Fact]
    public void ProfileConfiguration_IsValid()
    {
        // Catches unmapped properties, missing ForCtorParam, type mismatches.
        // If this fails after adding a field to AccountDto, you forgot to map it.
        var config = new MapperConfiguration(cfg => cfg.AddMaps(typeof(AccountProfile).Assembly), NullLoggerFactory.Instance);
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Account_MapsTo_AccountDto_Correctly()
    {
        var account = new Account
        {
            Id = 1,
            AccountNumber = "EQB001",
            HolderName = "Alice",
            Balance = 10000m,
            Transactions =
            [
                new Transaction { Id = 1, AccountId = 1, Amount = 100m, Timestamp = DateTime.UtcNow, Description = "Test" },
                new Transaction { Id = 2, AccountId = 1, Amount = 200m, Timestamp = DateTime.UtcNow, Description = "Test 2" }
            ]
        };

        var dto = _mapper.Map<AccountDto>(account);

        dto.AccountNumber.Should().Be("EQB001");
        dto.HolderName.Should().Be("Alice");
        dto.Balance.Should().Be(10000m);
        dto.TransactionCount.Should().Be(2); // Computed from Transactions.Count
    }

    [Fact]
    public void Account_WithNoTransactions_MapsTransactionCountAsZero()
    {
        var account = new Account
        {
            Id = 1,
            AccountNumber = "EQB001",
            HolderName = "Alice",
            Balance = 0m,
            Transactions = []
        };

        var dto = _mapper.Map<AccountDto>(account);

        dto.TransactionCount.Should().Be(0);
    }
}
