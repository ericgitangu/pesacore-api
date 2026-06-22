# variables.tf — inputs. Defaults mirror scripts/deploy.sh exactly so a `plan`
# describes the same live stack (project noobea, region africa-south1).

variable "project" {
  description = "GCP project ID. Mirrors scripts/deploy.sh PROJECT default."
  type        = string
  default     = "noobea"
}

variable "region" {
  description = "GCP region for Cloud Run + Artifact Registry. Mirrors deploy.sh REGION."
  type        = string
  default     = "africa-south1"
}

variable "image_tag" {
  description = "Image tag for the api/web images in Artifact Registry. deploy.sh pins :latest."
  type        = string
  default     = "latest"
}

# COST GATE. Default false keeps the global forwarding rule (GCLB, ~$18+/mo even
# at zero traffic) and Cloud Armor (~$5/mo + rules) out of every plan/apply.
# Flipping this to true is the ONLY way the cost-incurring edge would ever bill —
# see the documented review trigger. Leave false in this repo.
variable "enable_managed_edge" {
  description = "Gate the cost-incurring GCLB + Cloud Armor edge. Keep FALSE — see the design notes."
  type        = bool
  default     = false
}

# Names of the Secret Manager secrets that already exist (created out-of-band /
# by deploy.sh). Terraform REFERENCES these by name and never rotates them.
variable "postgres_secret_name" {
  description = "Existing Secret Manager secret holding the Postgres connection string."
  type        = string
  default     = "pesacore-postgres"
}

variable "redis_secret_name" {
  description = "Existing Secret Manager secret holding the Upstash Redis connection string."
  type        = string
  default     = "pesacore-redis"
}

# deploy.sh caps the API at 1 instance when no Redis secret exists (in-memory
# idempotency is only correct single-instance) and 3 when distributed Redis is
# present. IaC assumes the Redis-present path; expose the cap as a knob.
variable "api_max_instances" {
  description = "Max API instances. deploy.sh uses 3 with distributed Redis, 1 without."
  type        = number
  default     = 3
}

variable "web_max_instances" {
  description = "Max web/BFF instances. Mirrors deploy.sh (3)."
  type        = number
  default     = 3
}
