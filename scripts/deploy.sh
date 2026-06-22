#!/usr/bin/env bash
# PesaCore — Cloud Run deploy. Secrets live ONLY in GCP Secret Manager (the vault);
# Cloud Run reads them at runtime via --set-secrets. Nothing sensitive is passed on
# the command line, baked into an image, or committed. Idempotent + re-runnable.
#
#   PROJECT=noobea REGION=africa-south1 ./scripts/deploy.sh
set -euo pipefail

PROJECT="${PROJECT:-noobea}"
REGION="${REGION:-africa-south1}"
AR="${REGION}-docker.pkg.dev/${PROJECT}/pesacore"

echo ">> [1/4] Build + push images (Cloud Build, native linux/amd64)"
gcloud builds submit --config cloudbuild.yaml --project "$PROJECT" .

# --- API: reads Postgres (always) + Redis (only if the Upstash secret exists) from the vault ---
API_SECRETS="ConnectionStrings__Postgres=pesacore-postgres:latest"
if gcloud secrets describe pesacore-redis --project "$PROJECT" >/dev/null 2>&1; then
  API_SECRETS="${API_SECRETS},ConnectionStrings__Redis=pesacore-redis:latest"
  MAXI=3
  echo ">> Upstash secret present -> distributed Redis cache/idempotency enabled"
else
  MAXI=1   # in-memory idempotency is only correct on a single instance
  echo ">> No pesacore-redis secret yet -> in-memory fallback; capping API at 1 instance"
fi

echo ">> [2/4] Deploy API (secrets mounted from Secret Manager)"
gcloud run deploy pesacore-api \
  --image "${AR}/api:latest" --region "$REGION" --project "$PROJECT" \
  --allow-unauthenticated --min-instances 0 --max-instances "$MAXI" --port 8080 \
  --set-secrets "$API_SECRETS" \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production,AllowedHosts=*"
  # ASPNETCORE_ENVIRONMENT=Production: disables dev-only sensitive logging + the OTel
  #   console exporter (log flood) + Scalar. AllowedHosts=*: appsettings.Production.json
  #   restricts hosts to the (fictional) bank domains for realism; the *.run.app domain
  #   isn't in that list, so we override here (the BFF + Cloud Run edge front it anyway).

API_URL="$(gcloud run services describe pesacore-api --region "$REGION" --project "$PROJECT" --format='value(status.url)')"
echo ">> API live at: $API_URL"

echo ">> [3/4] Deploy web/BFF (proxies /api -> API; no secrets, just the upstream URL)"
gcloud run deploy pesacore-web \
  --image "${AR}/web:latest" --region "$REGION" --project "$PROJECT" \
  --allow-unauthenticated --min-instances 0 --max-instances 3 --port 8080 \
  --set-env-vars "PesaCore__BaseUrl=${API_URL}"

WEB_URL="$(gcloud run services describe pesacore-web --region "$REGION" --project "$PROJECT" --format='value(status.url)')"
echo ">> [4/4] LIVE: ${WEB_URL}  (docs: ${WEB_URL}/docs)"
