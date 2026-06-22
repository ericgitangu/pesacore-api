# PesaCore — .NET 10 Cloud-Native Banking Platform

[![CI](https://github.com/ericgitangu/pesacore-api/actions/workflows/ci.yml/badge.svg)](https://github.com/ericgitangu/pesacore-api/actions/workflows/ci.yml)
![Tests](https://img.shields.io/badge/tests-70%20passing-brightgreen)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-13-239120?logo=csharp&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-blue)

> A deployed, full-stack, cloud-native core-banking demo: a **Blazor WebAssembly** console fronted by an **ASP.NET Core BFF**, **CQRS** with idempotent money movement, real **Postgres** persistence + **distributed Redis** cache, end-to-end **observability**, a **hardened edge**, a **local Kubernetes** tier, and **Terraform** IaC — running serverless on **GCP Cloud Run** (scale-to-zero) and portable to on-prem **Windows / IIS**. One binary, three front doors.

### Application & framework
![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-10-512BD4?logo=dotnet&logoColor=white)
![Blazor WASM](https://img.shields.io/badge/Blazor-WebAssembly-512BD4?logo=blazor&logoColor=white)
![YARP](https://img.shields.io/badge/YARP-BFF_reverse_proxy-0078D4)
![CQRS](https://img.shields.io/badge/CQRS-MediatR-orange)
![EF Core](https://img.shields.io/badge/EF_Core-+_Dapper-512BD4)
![FluentValidation](https://img.shields.io/badge/FluentValidation-boundary-success)

### Data & cache
![Postgres](https://img.shields.io/badge/Neon-Postgres-336791?logo=postgresql&logoColor=white)
![Redis](https://img.shields.io/badge/Upstash-Redis-DC382D?logo=redis&logoColor=white)

### Observability
![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-OTLP-425CC7?logo=opentelemetry&logoColor=white)
![Jaeger](https://img.shields.io/badge/Jaeger-traces%2FAPM-66CFE3?logo=jaeger&logoColor=black)
![Prometheus](https://img.shields.io/badge/Prometheus-metrics-E6522C?logo=prometheus&logoColor=white)
![Grafana](https://img.shields.io/badge/Grafana-dashboards-F46800?logo=grafana&logoColor=white)

### Edge, orchestration & infrastructure
![nginx](https://img.shields.io/badge/nginx-TLS%2FHSTS%2FCSP%2Frate--limit%2FLB-009639?logo=nginx&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Linux%20%2B%20Windows%2FIIS-2496ED?logo=docker&logoColor=white)
![Kubernetes](https://img.shields.io/badge/Kubernetes-k3d%20%2F%20Kind%20%2B%20HPA-326CE5?logo=kubernetes&logoColor=white)
![Terraform](https://img.shields.io/badge/Terraform-GCP_IaC-7B42BC?logo=terraform&logoColor=white)
![Cloud Run](https://img.shields.io/badge/GCP-Cloud_Run_(scale--to--zero)-4285F4?logo=googlecloud&logoColor=white)
![Husky.Net](https://img.shields.io/badge/Husky.Net-pre--commit%2Fpush_gates-FFAC45)

---

## What it does
Open accounts, move money between them, view balances — with the correctness properties a payments backend actually needs:
- **Idempotent mutations** — every transfer/create carries an `X-Idempotency-Key`; a retry (even on a different instance) replays the cached result instead of double-charging.
- **Boundary validation** — FluentValidation on the command → RFC 7807 problem responses, before any handler runs.
- **Concurrency-safe** — transfers run in a DB transaction; account-number allocation retries on unique-constraint races.
- **Distributed by default** — idempotency keys + read-cache live in Redis, so horizontal scale and scale-to-zero are safe.

## Architecture
```
Browser (Blazor WASM SPA)
   │  HTTPS
   ▼
nginx edge ── TLS 1.2/1.3 · HSTS · CSP · rate-limit · gzip · upstream LB
   ▼
ASP.NET Core BFF (YARP) ── serves the SPA, reverse-proxies /api (no token/URL in the browser)
   ▼
PesaCore API (CQRS / MediatR) ──► Neon Postgres (durable state)
   │                          └──► Upstash Redis (idempotency + cache-aside)
   └─ OTLP ─► OpenTelemetry Collector ─► Jaeger (traces) + Prometheus (metrics) ─► Grafana
```
**Recurring principle:** a *stateless, portable core*; *state, infrastructure, and hardening at the configurable edge*. The vendor (Neon/Upstash, nginx/GCLB) sits behind an abstraction, so it's a deployment choice — not a code dependency.

## One binary, three front doors
The same `PesaCore.dll` runs under:
- **Kestrel** in a Linux container (Cloud Run / local Docker),
- **IIS + ANCM** on **Windows Server 2022** on-prem (`web.config`, `IISProfile.pubxml`, .NET 10 Hosting Bundle),
- **Azure** App Service / Container Apps.

## Run it locally (`make` + `docker`)
```bash
make up         # API :8080  +  Blazor WASM/BFF :8090
make obs-up     # OpenTelemetry → Jaeger :16686 + Prometheus :9090 → Grafana :3000
make edge-up    # hardened nginx edge → https://localhost:8443
make cluster-up # local k8s (k3d preferred / Kind fallback) + ingress-nginx + HPA
make test       # 70 tests (xUnit + WebApplicationFactory)
```
The app runs standalone with **SQLite + in-memory cache** when no Postgres/Redis is configured; set `ConnectionStrings:Postgres` / `:Redis` to switch (both behind `EF Core` / `IDistributedCache`).

## Deploy (GCP Cloud Run, scale-to-zero)
```bash
./scripts/deploy.sh   # Cloud Build (amd64) → Artifact Registry → Cloud Run; secrets from Secret Manager
```
Infrastructure is also captured as **Terraform** under [`infra/terraform/`](infra/terraform/) — authored and **cost-gated** (the managed load balancer + WAF are behind a default-off flag, so idle spend stays ~$0).

## Tech stack
| Layer | Tech |
|---|---|
| Runtime / API | .NET 10 · ASP.NET Core · C# · CQRS (MediatR) · FluentValidation · Polly · EF Core (+ Dapper) · AutoMapper · Serilog |
| Front end | Blazor WebAssembly SPA · ASP.NET Core BFF (YARP reverse proxy) · ReDoc / OpenAPI |
| Data | Neon Postgres (durable) · Upstash Redis (idempotency + cache) — serverless, scale-to-zero |
| Observability | OpenTelemetry → Collector → Jaeger (traces/APM) + Prometheus (metrics) → Grafana |
| Edge | nginx reverse-proxy / API gateway — TLS 1.2/1.3, HSTS, CSP, rate-limiting, gzip, upstream load-balancing |
| Orchestration | Docker (Linux + Windows/IIS) · Kubernetes (k3d / Kind) + ingress-nginx + HPA |
| Cloud / IaC | GCP Cloud Run · Cloud Build · Artifact Registry · Secret Manager · Terraform |
| Quality gates | xUnit · WebApplicationFactory · Husky.Net (pre-commit format + secret scan, pre-push build + test) |

## License
MIT — see [LICENSE](LICENSE).
