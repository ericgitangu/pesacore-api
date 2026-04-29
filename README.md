# PesaCore API

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-10-512BD4?logo=dotnet&logoColor=white)

Core-banking accounts API in **.NET 10** — CQRS via MediatR, EF Core (SQLite +
InMemory), Dapper for hot-path reads, FluentValidation, Polly for resilience,
Serilog for structured logging, Scalar for OpenAPI. Intentionally Africa-aware
in design (multi-currency, partner-bank integration patterns) without locking
to any one provider.

## Stack

| Concern | Choice | Why |
|---|---|---|
| Framework | ASP.NET Core 10 | Native nullable reference types, minimal APIs where they earn their keep |
| Mediation | MediatR 14 | Commands + queries cleanly separated; pipeline behaviors for cross-cutting |
| ORM | EF Core 10 (SQLite + InMemory) | InMemory for tests, SQLite for local, swap to Postgres in prod |
| Hot-path reads | Dapper 2.1 | Where EF's tracking gets in the way |
| Validation | FluentValidation 11 | Composable rules, integrates cleanly with MediatR pipeline |
| Resilience | Polly 8 | Retries, circuit breakers, timeouts on partner-bank calls |
| Logging | Serilog 10 | Structured logs with correlation IDs, ready for Seq / ELK |
| OpenAPI | Scalar 2.14 | Modern docs UI, no Swashbuckle drag |

## Architecture

CQRS at the seams, MediatR pipeline behaviors for cross-cutting concerns:

```
HTTP request
  ↓
Controller (thin)
  ↓
MediatR.Send(command|query)
  ↓
Pipeline behaviors:
  ├─ ValidationBehavior      (FluentValidation)
  ├─ IdempotencyBehavior     (database-backed for at-least-once safety)
  ├─ CorrelationIdBehavior   (propagated via Serilog LogContext)
  └─ RetryBehavior           (Polly, on transient transport errors)
  ↓
Handler
  ├─ Reads: Dapper → IDbConnection
  └─ Writes: EF Core DbContext → SaveChangesAsync
  ↓
Response DTO (mapped via AutoMapper)
```

## Features

- `GetAccountBalance` — query handler, Dapper-backed read
- `TransferFunds` — command handler with idempotency key, validates source/destination, atomic write
- Idempotency persists outcomes — replays return the original response without re-executing the side effect
- Correlation IDs flow through every log line, surface in response headers, and feed Serilog's `LogContext`
- Polly retry policies on outbound partner-bank calls

## Running locally

```bash
dotnet restore
dotnet build
dotnet run --project src/PesaCore.Api
```

OpenAPI explorer at `http://localhost:5000/scalar/v1` (Scalar — replace
Swashbuckle's grey UI with something readable).

```bash
# tests
dotnet test
```

## Project layout

```
src/PesaCore.Api/
  Behaviors/        MediatR pipeline behaviors (idempotency, validation)
  Controllers/      thin HTTP entry points
  Data/             EF Core DbContext + Dapper connection factory
  Dtos/             request/response DTOs
  Features/         vertical slices: command + handler co-located
  Mappers/          AutoMapper profiles
  Middleware/       correlation ID, exception handling
  Models/           domain entities
  Services/         partner-bank gateways, time providers, etc.
  Validators/       FluentValidation validators
tests/PesaCore.Api.Tests/
  ...mirrors src/ structure
```

## Why "PesaCore"

"Pesa" is Swahili for money. "Core" because this is a core-banking primitives
API — accounts, transfers, idempotent commands. The codebase is
deliberately positioned for African market patterns (multi-currency
floats, partner-bank webhook signatures, mobile-money rails) without
hard-coding to any one provider.

---

© 2026 Eric Gitangu — MIT licensed.
