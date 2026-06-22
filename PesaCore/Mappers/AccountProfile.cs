using AutoMapper;
using PesaCore.Models;
using PesaCore.Dtos;

namespace PesaCore.Mappers;

// AutoMapper Profile — a class that declares how to map between types.
// One Profile per domain area is the convention (AccountProfile, TransactionProfile, etc.).
// At startup, AddAutoMapper scans the assembly for all Profile subclasses,
// builds an immutable MapperConfiguration, and registers IMapper as a Singleton.
// Java equivalent: ModelMapper or MapStruct (MapStruct generates code at compile time,
// AutoMapper resolves at runtime — trade-off: magic vs boilerplate).
// Python equivalent: no direct equivalent — you'd write a to_dict() method or use Pydantic.
public class AccountProfile : Profile
{
    public AccountProfile()
    {
        // CreateMap<Source, Destination> — declares the mapping direction.
        // AutoMapper auto-maps properties with matching names and types:
        //   Account.AccountNumber (string) → AccountDto.AccountNumber (string) ✓ auto
        //   Account.HolderName (string)    → AccountDto.HolderName (string)    ✓ auto
        //   Account.Balance (decimal)      → AccountDto.Balance (decimal)      ✓ auto
        //   Account.??? → AccountDto.TransactionCount                          ✗ no match
        //
        // ForCtorParam — needed because TransactionCount is computed, not a direct property.
        // The DTO is a positional record, so its constructor takes TransactionCount as a param.
        // ForCtorParam tells AutoMapper: "for the constructor parameter named TransactionCount,
        // compute it from src.Transactions.Count."
        // If the DTO were a class with settable properties, you'd use ForMember instead.
        CreateMap<Account, AccountDto>()
            .ForCtorParam(nameof(AccountDto.TransactionCount),
                opt => opt.MapFrom(src => src.Transactions.Count));
    }
}
