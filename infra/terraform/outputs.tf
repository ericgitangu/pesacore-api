# outputs.tf — service URLs. The Cloud Run *.run.app URLs are always present
# (core stack); the GCLB IP only exists when enable_managed_edge=true.

output "api_url" {
  description = "pesacore-api Cloud Run service URL (the run.app endpoint deploy.sh prints)."
  value       = google_cloud_run_v2_service.api.uri
}

output "web_url" {
  description = "pesacore-web/BFF Cloud Run service URL — the public entrypoint (/docs lives here)."
  value       = google_cloud_run_v2_service.web.uri
}

output "managed_edge_ip" {
  description = "GCLB global forwarding-rule IP. null unless enable_managed_edge=true (cost gate)."
  value       = var.enable_managed_edge ? google_compute_global_forwarding_rule.web_fr[0].ip_address : null
}

output "managed_edge_enabled" {
  description = "Echoes the cost gate so a plan/output makes the $0-vs-billing state explicit."
  value       = var.enable_managed_edge
}
