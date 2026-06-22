# PesaCore.Web — BFF + Blazor WebAssembly front end

A bespoke, dark, financial-grade SPA for the PesaCore core-banking API.

- **`PesaCore.Web.Client`** — Blazor WebAssembly (net10.0). Pure client-side SPA.
- **`PesaCore.Web`** — ASP.NET Core BFF host (net10.0). Serves the WASM payload
  *and* reverse-proxies `/api/*` to PesaCore via **YARP**, keeping the API base
  URL server-side. The browser only ever talks to this origin, so there is **no
  CORS** and **no API URL/secret in the client**.

```
browser ──▶ PesaCore.Web (BFF)
              ├─ static: index.html + .wasm/.dll/.css   (UseBlazorFrameworkFiles)
              └─ /api/*  ──YARP──▶  PesaCore API   (PathRemovePrefix "/api")
```

## Screens

| Route                     | Purpose                                                        |
|---------------------------|----------------------------------------------------------------|
| `/`                       | Accounts dashboard — card per account, balance, tx count, staggered entrance |
| `/accounts/{accountNumber}` | Account detail — holder, number, live balance                |
| `/transfer`               | Fund transfer — validated form, generated `X-Idempotency-Key`, live receipt + updated balance |

## API contracts wired against (PesaCore)

| Client call | Proxied to | Shape |
|---|---|---|
| `GET /api/Accounts/dto/linq` | `GET /Accounts/dto/linq` | `[{accountNumber, holderName, balance, transactionCount}]` |
| `GET /api/Cqrs/balance/{n}`  | `GET /Cqrs/balance/{n}`  | `{accountNumber, holderName, balance}` or `404` |
| `POST /api/Cqrs/transfer`    | `POST /Cqrs/transfer`    | body `{fromAccount, toAccount, amount}` + header `X-Idempotency-Key` → `{success, message, newBalance}` |

The transfer endpoint **requires** `X-Idempotency-Key` (PesaCore's
`IdempotencyBehavior` rejects keyless mutations with `400`). The client
generates a fresh UUID v4 per submit.

## Run locally (two processes)

```bash
# 1) API on :5235
ASPNETCORE_ENVIRONMENT=Development dotnet run --project PesaCore --launch-profile http

# 2) BFF + SPA on :5182 (proxies to PesaCore:BaseUrl = http://localhost:5235)
dotnet run --project PesaCore.Web
#   open http://localhost:5182
```

`PesaCore:BaseUrl` is the single config knob for the upstream:
- local dotnet → `http://localhost:5235` (`appsettings.json`)
- docker       → `http://pesacore:8080` (`PesaCore__BaseUrl` env)
- Cloud Run    → injected as `PesaCore__BaseUrl`

## Run with Docker Compose

```bash
dc up --build pesacore pesacore-web      # `dc` = docker compose
#   API at  http://localhost:8080
#   SPA at  http://localhost:8090
```

`pesacore-web` listens on `$PORT` (default 8080; Cloud Run sets it), runs as the
non-root `app` user, and `HEALTHCHECK`s `/healthz`. Response compression
(Brotli + gzip) is enabled; the WASM publish also emits precompressed
`_framework` assets (43 `.br` + 43 `.gz`).

## Styling — Tailwind standalone vs the hand-written fallback (current)

The brief asked to try the **Tailwind standalone CLI** (no npm/node) first.
**Network egress is blocked in the build environment**, so the binary could not
be fetched. Per the documented fallback, the UI uses a **hand-written modern CSS
design system** (`wwwroot/css/app.css` + per-component `.razor.css`) using custom
properties — no framework, no npm/node. Fonts: Fraunces (display) / IBM Plex
Sans (body) / IBM Plex Mono (tabular figures).

To switch to Tailwind standalone in an environment with egress:

```bash
# 1) fetch the standalone binary (macOS arm64 shown; pick your platform)
curl -sL -o PesaCore.Web.Client/tools/tailwindcss \
  https://github.com/tailwindlabs/tailwindcss/releases/latest/download/tailwindcss-macos-arm64
chmod +x PesaCore.Web.Client/tools/tailwindcss

# 2) tailwind.config.js content globs (scan Razor + html):
#    content: ["./**/*.razor", "./wwwroot/index.html"]

# 3) build app.css from a Tailwind entry file:
PesaCore.Web.Client/tools/tailwindcss \
  -i PesaCore.Web.Client/Styles/app.tailwind.css \
  -o PesaCore.Web.Client/wwwroot/css/app.css --minify
```

Wire step 3 into the client `.csproj` as a `BeforeTargets="Build"` exec so it
runs on every build. The current bespoke `app.css` would be replaced by the
Tailwind output.

## Tests (the empirical proof)

`PesaCore.Tests/Integration/BffProxyIntegrationTests.cs` boots a **real**
PesaCore API on a loopback port and a **real** YARP gateway with the identical
route/cluster config, then asserts the proxied responses over real HTTP —
exercising the actual proxy code path, not a mock.

```bash
dotnet test PesaCore.Tests/PesaCore.Tests.csproj   # 53/53
```
