# PesaCore — Terraform IaC (authored, NOT applied)

> ## ⚠️ DO NOT `terraform apply` — COST. `init` / `plan` only.
>
> Real spend must stay **$0**. The live services were stood up by
> [`scripts/deploy.sh`](../../scripts/deploy.sh) (gcloud); this Terraform is the
> *reproducible target*, documented and validated but never applied here.
> `terraform validate` and `terraform fmt` touch nothing in the cloud and are the
> only commands run against this directory. See
> the project design notes.

## What this provisions

| File | Resources |
|---|---|
| `versions.tf` | terraform `>= 1.5`, google provider `~> 6.0`; provider points at `noobea` / `africa-south1` |
| `variables.tf` | `project` (`noobea`), `region` (`africa-south1`), `image_tag` (`latest`), `enable_managed_edge` (**bool, default false** — the cost gate) |
| `main.tf` | Artifact Registry repo `pesacore`; Cloud Run `pesacore-api` + `pesacore-web` (both **min-instances 0**, scale-to-zero); secret env from existing Secret Manager secrets; `allUsers` invoker IAM |
| `edge.tf` | GCLB (serverless NEG → backend → URL map → HTTPS proxy → global forwarding rule) + Cloud Armor — **all gated `count = var.enable_managed_edge ? 1 : 0`** |
| `outputs.tf` | `api_url`, `web_url`, `managed_edge_ip` (null unless gate on), `managed_edge_enabled` |

The existing secrets `pesacore-postgres` / `pesacore-redis` are referenced as
**data sources** — Terraform reads them, never recreates or rotates them. No
secret values appear in this code; only the secret *names*.

## Cost table (from )

| Resource | Idle cost | Note |
|---|---|---|
| Cloud Run (min-instances=0) | **~$0** | pay-per-request; free tier covers a demo |
| Artifact Registry | ~$0 | <0.5 GB free; cleanup policy keeps 5 recent |
| Secret Manager | ~$0 | 6 free secret-versions/active |
| Neon / Upstash | **$0** | serverless free tier |
| **GCLB (global forwarding rule)** | **~$18+/mo** | bills **even at zero traffic** — `edge.tf`, gated off |
| **Cloud Armor (WAF)** | **~$5/mo + rules** | `edge.tf`, gated off |
| min-instances ≥ 1 | ~$ per instance-hour | not used — we stay at 0 |

**The cost-incurring hardening (GCLB + Cloud Armor) is exactly what the local
nginx edge demonstrates for free.** Default `enable_managed_edge=false` means a
`plan` shows **0** edge resources and an `apply` could never bill for them.

## Commands (safe — no cloud mutation)

```bash
# one-time, no remote backend, no provider auth needed for validate
terraform -chdir=infra/terraform init -backend=false
terraform -chdir=infra/terraform fmt -check
terraform -chdir=infra/terraform validate

# plan (read-only; needs ADC creds to enumerate state). Default gate = $0 edge:
terraform -chdir=infra/terraform plan                          # 0 edge resources
terraform -chdir=infra/terraform plan -var enable_managed_edge=true  # shows GCLB+Armor, still NOT applied
```

`apply` is intentionally undocumented as a runnable step. Flipping the gate is
the **the documented review trigger** — only when real traffic/SLA justifies an
always-on managed WAF, and only after `terraform import` reconciles drift with
the live gcloud-deployed services.

## Local nginx ↔ cloud GCLB / Cloud Armor mapping

| Local (free, `edge/`) | Cloud (`edge.tf`, gated) | What it does |
|---|---|---|
| nginx TLS term (self-signed `edge/certs`) | `google_compute_managed_ssl_certificate` + HTTPS target proxy | terminate TLS at the edge |
| nginx `upstream` LB across replicas | GCLB backend service + serverless NEG | distribute to the web/BFF |
| `limit_req zone=api rate=10r/s` → 429 | Cloud Armor throttle on `/api/`, 10 r/s, `deny(429)` | tighter API rate-limit |
| `limit_req zone=general rate=30r/s` → 429 | Cloud Armor throttle (all), 30 r/s, `deny(429)` | general rate-limit |
| nginx 403 on disallowed hosts | Cloud Armor `deny(403)` default floor | reject unmatched traffic |
| security headers (`security-headers.inc`) | (set by the BFF / app — headers not re-expressed in GCLB) | CSP/HSTS |

The architecture point: **the same edge envelope expressed twice** — nginx
locally (free, demonstrative), GCLB + Cloud Armor in cloud (managed, billable).
