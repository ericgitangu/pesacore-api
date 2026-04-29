using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Dapper;
using System.Data;
using PesaCore.Api.Data;

namespace PesaCore.Api.Controllers;

// ===== DAPPER — raw SQL for reporting, alongside EF Core =====
//
// Dapper and EF Core are NOT competitors — they're a split:
//   EF Core: domain model, transactional writes, change tracking, navigation properties
//   Dapper:  reporting, reconciliation, complex SQL (CTEs, window functions, index hints)
//
// In banking, this split is the norm:
//   - Write path (transfers, deposits): EF Core — change tracking, validation, SaveChanges
//   - Read path (EOD reports, dashboards, CBK submissions): Dapper — raw SQL, maximum control
//
// Why not use EF Core for everything?
//   1. Complex aggregates (GROUP BY + HAVING + window functions) produce fragile LINQ
//   2. EF's SQL translator can't handle every SQL feature (PIVOT, LATERAL, CTE recursion)
//   3. Reporting queries don't need change tracking — it's wasted overhead
//   4. DBA-written reporting SQL can be pasted directly into Dapper — no translation step
//
// Why not use Dapper for everything?
//   1. No change tracking — you manage every INSERT/UPDATE manually
//   2. No navigation properties — joins are manual
//   3. No migrations — schema management is separate
//   4. Mapping boilerplate for complex object graphs
//
// Java equivalent: JDBC/JdbcTemplate for reporting alongside JPA/Hibernate for the domain model
// Python equivalent: raw SQL via psycopg2/asyncpg alongside SQLAlchemy ORM

[ApiController]
[Route("[controller]")]
public class ReportsController : ControllerBase
{
    private readonly BankDbContext _db;

    // Injecting DbContext for Dapper? Yes — we reuse the SAME connection.
    // _db.Database.GetDbConnection() returns the underlying DbConnection
    // that EF Core is already managing. This means:
    //   - No second connection string to configure
    //   - Connection pooling is shared
    //   - If you're inside a transaction, Dapper participates in it
    // This is the recommended pattern — don't create a separate connection for Dapper.
    public ReportsController(BankDbContext db)
    {
        _db = db;
    }

    // Reporting endpoint — aggregates across accounts and transactions.
    // This SQL uses GROUP BY + aggregate functions + COALESCE — exactly the kind
    // of query where Dapper shines and EF Core LINQ gets awkward.
    [HttpGet("account-totals")]
    public async Task<IActionResult> GetAccountTotals()
    {
        // GetDbConnection() returns the raw DbConnection (System.Data.Common).
        // Dapper extends DbConnection with .QueryAsync() — it's extension methods,
        // not a separate connection type. Dapper is literally just extension methods.
        var connection = _db.Database.GetDbConnection();

        // Dapper needs the connection open. EF may have already opened it,
        // but checking state is defensive — avoids "connection is closed" exceptions.
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        // Raw SQL — readable, auditable, paste-able into any SQL tool.
        // A DBA reviewing this query can understand it immediately.
        // Try expressing this in LINQ and compare readability.
        //
        // COALESCE: SQL's null-safe default — if SUM is null (no transactions), return 0.
        // Without COALESCE, accounts with no transactions would show NULL, not 0.
        const string sql = @"
            SELECT
                a.AccountNumber,
                a.HolderName,
                a.Balance,
                COUNT(t.Id) AS TransactionCount,
                COALESCE(SUM(t.Amount), 0) AS TotalTransacted
            FROM Accounts a
            LEFT JOIN Transactions t ON t.AccountId = a.Id
            GROUP BY a.Id, a.AccountNumber, a.HolderName, a.Balance
            ORDER BY a.AccountNumber";

        // QueryAsync returns IEnumerable<dynamic> — Dapper maps columns to dynamic properties.
        // For production, you'd use QueryAsync<AccountReportDto> with a typed DTO
        // for compile-time safety. Dynamic is fine for prototyping/learning.
        var results = await connection.QueryAsync(sql);
        return Ok(results);
    }
}
