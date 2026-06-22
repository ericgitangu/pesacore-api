# edge.tf — the COST-INCURRING managed edge: Global external HTTPS LB (GCLB) +
# Cloud Armor (WAF). This is the cloud equivalent of the local nginx edge
# (TLS term, headers, rate-limit).  GCLB bills ~$18+/mo even at zero
# traffic; Cloud Armor ~$5/mo + per-rule.
#
# EVERY resource here is gated `count = var.enable_managed_edge ? 1 : 0`.
# With the default (false) a plan shows 0 of these and an apply could never bill.
# DO NOT flip enable_managed_edge in this repo. See the documented review trigger.

locals {
  edge_count = var.enable_managed_edge ? 1 : 0
}

# --- Serverless NEG pointing at the web/BFF Cloud Run service ---
resource "google_compute_region_network_endpoint_group" "web_neg" {
  count                 = local.edge_count
  name                  = "pesacore-web-neg"
  region                = var.region
  network_endpoint_type = "SERVERLESS"

  cloud_run {
    service = google_cloud_run_v2_service.web.name
  }
}

# --- Cloud Armor: mirrors the nginx rate-limit (general 30 r/s, api 10 r/s,
#     status 429) plus an explicit default-deny floor + allow-all above it.
#     nginx zones -> Cloud Armor rate-based-ban rules keyed on client IP.
resource "google_compute_security_policy" "pesacore_waf" {
  count       = local.edge_count
  name        = "pesacore-waf"
  description = "WAF mirroring the local nginx edge (rate-limit + deny floor)."

  # /api/* — tighter limit, mirrors nginx `zone=api rate=10r/s`.
  rule {
    action   = "throttle"
    priority = 1000
    match {
      expr {
        expression = "request.path.startsWith('/api/')"
      }
    }
    rate_limit_options {
      conform_action = "allow"
      exceed_action  = "deny(429)" # nginx limit_req_status 429
      enforce_on_key = "IP"
      rate_limit_threshold {
        count        = 10 # 10 r/s, mirrors nginx api zone
        interval_sec = 1
      }
    }
  }

  # everything else — general limit, mirrors nginx `zone=general rate=30r/s`.
  rule {
    action   = "throttle"
    priority = 2000
    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
    rate_limit_options {
      conform_action = "allow"
      exceed_action  = "deny(429)"
      enforce_on_key = "IP"
      rate_limit_threshold {
        count        = 30 # 30 r/s, mirrors nginx general zone
        interval_sec = 1
      }
    }
  }

  # explicit deny floor (the WAF analogue of nginx returning 403 on bad hosts).
  # Required default rule for a security policy; priorities above let traffic in.
  rule {
    action   = "deny(403)"
    priority = 2147483647
    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
    description = "Default deny floor — overridden by the throttle rules above."
  }
}

# --- Backend service: web NEG + Cloud Armor attached ---
resource "google_compute_backend_service" "web_backend" {
  count                 = local.edge_count
  name                  = "pesacore-web-backend"
  load_balancing_scheme = "EXTERNAL_MANAGED"
  protocol              = "HTTPS"
  security_policy       = google_compute_security_policy.pesacore_waf[0].id

  backend {
    group = google_compute_region_network_endpoint_group.web_neg[0].id
  }
}

# --- URL map -> backend ---
resource "google_compute_url_map" "web_urlmap" {
  count           = local.edge_count
  name            = "pesacore-web-urlmap"
  default_service = google_compute_backend_service.web_backend[0].id
}

# --- Google-managed TLS cert (the GCLB TLS-term, cloud analogue of nginx certs) ---
resource "google_compute_managed_ssl_certificate" "web_cert" {
  count = local.edge_count
  name  = "pesacore-web-cert"
  managed {
    # Placeholder domain — a real apply would set the production hostname.
    domains = ["pesacore.example.com"]
  }
}

resource "google_compute_target_https_proxy" "web_proxy" {
  count            = local.edge_count
  name             = "pesacore-web-https-proxy"
  url_map          = google_compute_url_map.web_urlmap[0].id
  ssl_certificates = [google_compute_managed_ssl_certificate.web_cert[0].id]
}

# --- Global forwarding rule: THIS is the ~$18+/mo line item (bills at idle) ---
resource "google_compute_global_forwarding_rule" "web_fr" {
  count                 = local.edge_count
  name                  = "pesacore-web-fr"
  load_balancing_scheme = "EXTERNAL_MANAGED"
  target                = google_compute_target_https_proxy.web_proxy[0].id
  port_range            = "443"
}
