# PesaCore — workspace Makefile. Run every target from the repo root.
# Mirrors a Windows / Visual Studio solution workflow: one .slnx ties the
# API, tests, BFF host, and WASM client; these targets are the CLI equivalents
# of VS's Build / Test / Run / Publish, plus container + deploy verbs.
#
#   make            # list targets
#   make test       # build + run all tests across the solution
#   make run        # API + BFF/WASM together (local dotnet)
#   make up         # both in Docker (compose)

SLN     := PesaCore.slnx
API     := PesaCore/PesaCore.csproj
WEB     := PesaCore.Web/PesaCore.Web.csproj
CONFIG  ?= Debug
API_URL ?= http://localhost:5235

.DEFAULT_GOAL := help
.PHONY: help restore build rebuild clean test run run-api run-web watch-api watch-web \
        format publish up down logs obs-up obs-down deploy

help: ## List available targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
	  | awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-11s\033[0m %s\n",$$1,$$2}'

restore: ## Restore NuGet packages for the solution
	dotnet restore $(SLN)

build: ## Build the whole solution
	dotnet build $(SLN) -c $(CONFIG)

rebuild: clean build ## Clean then build

clean: ## Clean build outputs (bin/obj across the workspace)
	-dotnet clean $(SLN) -c $(CONFIG)
	find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

test: ## Build + run all tests (API unit/integration + BFF proxy)
	dotnet test $(SLN) -c $(CONFIG)

run-api: ## Run the API only (Kestrel :5235)
	ASPNETCORE_ENVIRONMENT=Development dotnet run --project $(API) --launch-profile http

run-web: ## Run the BFF + WASM only (:5182), proxying /api -> $(API_URL)
	PesaCore__BaseUrl=$(API_URL) dotnet run --project $(WEB)

run: ## Run API (background) + BFF/WASM (foreground) together
	@echo ">> API on :5235 (bg), BFF/WASM next (fg). Ctrl-C stops the BFF; then: pkill -f PesaCore"
	@(ASPNETCORE_ENVIRONMENT=Development dotnet run --project $(API) --launch-profile http >/tmp/pesacore-api.log 2>&1 &) ; \
	sleep 6 ; PesaCore__BaseUrl=$(API_URL) dotnet run --project $(WEB)

watch-api: ## Hot-reload the API
	dotnet watch --project $(API) run

watch-web: ## Hot-reload the BFF/WASM
	dotnet watch --project $(WEB) run

format: ## Apply dotnet format across the solution
	dotnet format $(SLN)

hooks: ## Install Husky.Net git hooks (pre-commit format+gitleaks, pre-push build+test)
	dotnet tool restore && dotnet husky install

publish: ## Release-publish the BFF+WASM (trimmed + Brotli) to ./artifacts/web
	dotnet publish $(WEB) -c Release -o ./artifacts/web /p:UseAppHost=false

up: ## Docker compose: API + BFF/WASM (API :8080, SPA :8090)
	docker compose up --build pesacore pesacore-web

down: ## Docker compose down
	docker compose down

logs: ## Tail compose logs
	docker compose logs -f

obs-up: ## Start local observability (otel-collector + prometheus + grafana) [added by infra step]
	docker compose --profile observability up -d

obs-down: ## Stop observability stack
	docker compose --profile observability down

cert: ## Generate the self-signed localhost cert for the nginx edge (once)
	openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
	  -keyout edge/certs/localhost.key -out edge/certs/localhost.crt \
	  -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"

edge-up: ## Tier B: nginx reverse-proxy/APIGW + LB + hardening (https://localhost:8443)
	@[ -f edge/certs/localhost.crt ] || $(MAKE) cert
	docker compose --profile edge up -d

edge-down: ## Stop the nginx edge
	docker compose --profile edge down

cluster-up: ## Tier C: spin up a local k3d/kind cluster (Deployments+Ingress+HPA)
	./scripts/cluster-up.sh

cluster-down: ## Tear down the local cluster
	./scripts/cluster-down.sh

deploy: ## Deploy API + web to Cloud Run; secrets read from GCP Secret Manager
	PROJECT=noobea REGION=africa-south1 ./scripts/deploy.sh
