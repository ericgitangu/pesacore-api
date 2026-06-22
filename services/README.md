# PesaCore — Service Catalog

The deployable units, what they run, and which IaC/target stands each up. The
app is **stateless** (state in Neon Postgres / Upstash Redis), so every service
scales horizontally and to zero.

## Services

### `pesacore-api`
| | |
|---|---|
| Role | ASP.NET Core API — the authenticated business surface |
| Image | `africa-south1-docker.pkg.dev/noobea/pesacore/api:latest` (built by [`cloudbuild.yaml`](../cloudbuild.yaml) from `Dockerfile`) |
| Port | `8080` |
| Env | `ASPNETCORE_ENVIRONMENT=Production`, `AllowedHosts=*` |
| Secrets | `ConnectionStrings__Postgres` ← Secret Manager `pesacore-postgres` (always); `ConnectionStrings__Redis` ← `pesacore-redis` (when present → distributed cache/idempotency, API may scale to 3; absent → in-memory, capped at 1) |
| Health | `/health` (and `/docs` for the Scalar UI in non-prod) |
| Scaling | min 0 / max 3 (1 if no Redis secret) |

### `pesacore-web`
| | |
|---|---|
| Role | Web / BFF — serves the SPA shell and proxies `/api` → the API |
| Image | `africa-south1-docker.pkg.dev/noobea/pesacore/web:latest` (built by `cloudbuild.yaml` from `Dockerfile.web`) |
| Port | `8080` |
| Env | `PesaCore__BaseUrl` = the resolved `pesacore-api` URL |
| Secrets | none |
| Health | `/` ; docs proxied at `/docs` |
| Scaling | min 0 / max 3 |

## Deploy targets — which IaC stands each up

| Target | File(s) | Notes |
|---|---|---|
| **Local (Docker Compose)** | `docker-compose.yml` + nginx edge in [`edge/`](../edge/) | free local stack; nginx = TLS term + LB + rate-limit |
| **Cloud Run (live)** | [`scripts/deploy.sh`](../scripts/deploy.sh) | **current truth** — gcloud-deployed; secrets via `--set-secrets` |
| **Cloud Run (IaC, authored)** | [`infra/terraform/`](../infra/terraform/) | reproducible target; `init`/`plan` only — **never applied** (cost) |
| **Kubernetes** | [`deploy/k8s/`](../deploy/k8s/) | Deployment + HPA + ingress-nginx variant |

The cloud LB/WAF hardening (GCLB + Cloud Armor) lives in
`infra/terraform/edge.tf`, **gated off by default** (`enable_managed_edge=false`)
because a GCLB forwarding rule bills even at idle — see
the project design notes and
[`infra/terraform/README.md`](../infra/terraform/README.md).
