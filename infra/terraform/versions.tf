# versions.tf — Terraform + provider pinning.
# Mirrors the global IaC default (Terraform 1.5.7 pinned) and a stable google
# provider major. Authored, not applied — see README.md.

terraform {
  required_version = ">= 1.5.0, < 2.0.0"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }
}

# No credentials block / no backend here on purpose: `init -backend=false` +
# `validate` is the only thing run in this repo. The project/region come from
# variables so a future real `apply` would target noobea / africa-south1.
provider "google" {
  project = var.project
  region  = var.region
}
