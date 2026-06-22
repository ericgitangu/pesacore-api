# main.tf — the always-on, ~$0 core: Artifact Registry, two scale-to-zero Cloud
# Run services, and the IAM that lets Cloud Run read existing secrets + serve
# unauthenticated traffic. This mirrors scripts/deploy.sh resource-for-resource.
#
# AUTHORED, NOT APPLIED. See README.md. The live services were stood up by
# scripts/deploy.sh (gcloud); this is the reproducible target, expected to drift.

locals {
  # AR path mirrors deploy.sh: ${REGION}-docker.pkg.dev/${PROJECT}/pesacore
  ar_host   = "${var.region}-docker.pkg.dev"
  ar_repo   = "pesacore"
  api_image = "${local.ar_host}/${var.project}/${local.ar_repo}/api:${var.image_tag}"
  web_image = "${local.ar_host}/${var.project}/${local.ar_repo}/web:${var.image_tag}"
}

# --- Artifact Registry: the pesacore Docker repo cloudbuild.yaml pushes to ---
resource "google_artifact_registry_repository" "pesacore" {
  location      = var.region
  repository_id = local.ar_repo
  description   = "PesaCore api/web container images (mirrors cloudbuild.yaml _AR)."
  format        = "DOCKER"

  # Keep AR under the <0.5 GB free tier: retain only the
  # most recent images. Authored — applies only on a real apply.
  cleanup_policies {
    id     = "keep-recent"
    action = "KEEP"
    most_recent_versions {
      keep_count = 5
    }
  }
}

# --- Existing secrets: REFERENCE only, never recreate/rotate ---
# deploy.sh expects pesacore-postgres (always) and pesacore-redis (optional).
# Data sources read them; if a future apply ever manages them, lifecycle guards
# below would prevent destructive rotation — but as data sources they are purely
# read-only, which is the safest expression of "do NOT recreate them".
data "google_secret_manager_secret" "postgres" {
  secret_id = var.postgres_secret_name
}

data "google_secret_manager_secret" "redis" {
  secret_id = var.redis_secret_name
}

# Cloud Run's per-service runtime identity needs secretAccessor on each secret.
# Using the default compute SA mirrors deploy.sh (which sets no custom SA).
data "google_project" "this" {}

locals {
  run_sa = "${data.google_project.this.number}-compute@developer.gserviceaccount.com"
}

resource "google_secret_manager_secret_iam_member" "postgres_accessor" {
  secret_id = data.google_secret_manager_secret.postgres.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${local.run_sa}"
}

resource "google_secret_manager_secret_iam_member" "redis_accessor" {
  secret_id = data.google_secret_manager_secret.redis.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${local.run_sa}"
}

# --- API service: secrets mounted as env from Secret Manager (deploy.sh §2) ---
resource "google_cloud_run_v2_service" "api" {
  name                = "pesacore-api"
  location            = var.region
  deletion_protection = false

  template {
    # scale-to-zero: min 0 = ~$0 idle.
    scaling {
      min_instance_count = 0
      max_instance_count = var.api_max_instances
    }

    containers {
      image = local.api_image
      ports {
        container_port = 8080 # deploy.sh --port 8080
      }

      # ASPNETCORE_ENVIRONMENT=Production,AllowedHosts=* — deploy.sh --set-env-vars
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "AllowedHosts"
        value = "*"
      }

      # ConnectionStrings__Postgres from Secret Manager (deploy.sh --set-secrets)
      env {
        name = "ConnectionStrings__Postgres"
        value_source {
          secret_key_ref {
            secret  = data.google_secret_manager_secret.postgres.secret_id
            version = "latest"
          }
        }
      }

      # ConnectionStrings__Redis — deploy.sh adds this only when the secret
      # exists; IaC assumes the distributed-cache path (api_max_instances=3).
      env {
        name = "ConnectionStrings__Redis"
        value_source {
          secret_key_ref {
            secret  = data.google_secret_manager_secret.redis.secret_id
            version = "latest"
          }
        }
      }
    }
  }

  depends_on = [
    google_secret_manager_secret_iam_member.postgres_accessor,
    google_secret_manager_secret_iam_member.redis_accessor,
  ]
}

# --- Web/BFF service: no secrets, just PesaCore__BaseUrl -> API url (deploy.sh §3) ---
resource "google_cloud_run_v2_service" "web" {
  name                = "pesacore-web"
  location            = var.region
  deletion_protection = false

  template {
    scaling {
      min_instance_count = 0
      max_instance_count = var.web_max_instances
    }

    containers {
      image = local.web_image
      ports {
        container_port = 8080
      }

      # deploy.sh sets PesaCore__BaseUrl to the API's resolved run.app URL.
      env {
        name  = "PesaCore__BaseUrl"
        value = google_cloud_run_v2_service.api.uri
      }
    }
  }
}

# --- allow-unauthenticated on both services (deploy.sh --allow-unauthenticated) ---
resource "google_cloud_run_v2_service_iam_member" "api_public" {
  name     = google_cloud_run_v2_service.api.name
  location = var.region
  role     = "roles/run.invoker"
  member   = "allUsers"
}

resource "google_cloud_run_v2_service_iam_member" "web_public" {
  name     = google_cloud_run_v2_service.web.name
  location = var.region
  role     = "roles/run.invoker"
  member   = "allUsers"
}
