#!/usr/bin/env bash
# PesaCore — Tier C: bring up a LOCAL k8s cluster and deploy the stack.
#
# This is a DEMO tier (ADR 0004): real orchestration on the laptop to show
# Deployments/replicas, Service discovery, Ingress, and HPA autoscaling. It is
# NOT the production path — prod is Cloud Run (see scripts/deploy.sh and
# docs/local_k8s_cluster.md). The cluster is throwaway; cluster-down.sh deletes it.
#
# Prereqs: docker running; kubectl; AND either k3d (preferred) or kind. The local
# app images `pesacore:linux` and `pesacore-web:linux` must already be built
# (`dc up --build` / `dc build` produces them — see docker-compose.yml).
#
#   ./scripts/cluster-up.sh
set -euo pipefail

CLUSTER="pesacore"
API_IMAGE="pesacore:linux"
WEB_IMAGE="pesacore-web:linux"
# Resolve the repo's k8s manifests relative to THIS script (cwd-independent).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
K8S_DIR="${SCRIPT_DIR}/../deploy/k8s"

# --- 0. preflight ----------------------------------------------------------
command -v kubectl >/dev/null 2>&1 || { echo "!! kubectl not found — install it first"; exit 1; }
command -v docker  >/dev/null 2>&1 || { echo "!! docker not found / not running"; exit 1; }

for img in "$API_IMAGE" "$WEB_IMAGE"; do
  if ! docker image inspect "$img" >/dev/null 2>&1; then
    echo "!! local image '$img' not found. Build it first: dc build  (or: dc up --build)"
    exit 1
  fi
done

# --- 1. pick a cluster runtime: k3d preferred, kind fallback ---------------
if command -v k3d >/dev/null 2>&1; then
  RUNTIME="k3d"
elif command -v kind >/dev/null 2>&1; then
  RUNTIME="kind"
else
  echo "!! neither k3d nor kind found. Install one:"
  echo "     brew install k3d     # preferred (k3s-in-docker)"
  echo "     brew install kind    # fallback"
  exit 1
fi
echo ">> [1/6] cluster runtime: ${RUNTIME}"

# --- 2. create the cluster --------------------------------------------------
# Map host :8080 -> the in-cluster ingress controller so the browser can reach it.
if [ "$RUNTIME" = "k3d" ]; then
  if k3d cluster list 2>/dev/null | grep -qw "$CLUSTER"; then
    echo ">> [2/6] k3d cluster '${CLUSTER}' already exists — reusing"
  else
    echo ">> [2/6] creating k3d cluster '${CLUSTER}' (disabling bundled traefik; we use ingress-nginx)"
    # --port 8080:80@loadbalancer routes host:8080 -> k3d serverlb -> ingress.
    k3d cluster create "$CLUSTER" \
      --port "8080:80@loadbalancer" \
      --k3s-arg "--disable=traefik@server:0" \
      --wait
  fi
else
  if kind get clusters 2>/dev/null | grep -qw "$CLUSTER"; then
    echo ">> [2/6] kind cluster '${CLUSTER}' already exists — reusing"
  else
    echo ">> [2/6] creating kind cluster '${CLUSTER}' (extraPortMapping 80->host 8080 for ingress)"
    cat <<'EOF' | kind create cluster --name pesacore --config -
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    kubeadmConfigPatches:
      - |
        kind: InitConfiguration
        nodeRegistration:
          kubeletExtraArgs:
            node-labels: "ingress-ready=true"
    extraPortMappings:
      - containerPort: 80
        hostPort: 8080
        protocol: TCP
EOF
  fi
fi

# Make sure kubectl talks to this cluster.
kubectl config use-context "$([ "$RUNTIME" = "k3d" ] && echo "k3d-${CLUSTER}" || echo "kind-${CLUSTER}")" >/dev/null

# --- 3. import the locally-built images into the cluster -------------------
# The cluster's containerd has no access to the host's docker images; import them
# so `imagePullPolicy: IfNotPresent` finds them without any registry.
echo ">> [3/6] importing local images: ${API_IMAGE}, ${WEB_IMAGE}"
if [ "$RUNTIME" = "k3d" ]; then
  k3d image import "$API_IMAGE" "$WEB_IMAGE" --cluster "$CLUSTER"
else
  kind load docker-image "$API_IMAGE" "$WEB_IMAGE" --name "$CLUSTER"
fi

# --- 4. install ingress-nginx ----------------------------------------------
echo ">> [4/6] installing ingress-nginx"
if [ "$RUNTIME" = "k3d" ]; then
  kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.11.3/deploy/static/provider/cloud/deploy.yaml
else
  kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.11.3/deploy/static/provider/kind/deploy.yaml
fi
echo ">> waiting for ingress-nginx controller to be ready..."
kubectl wait --namespace ingress-nginx \
  --for=condition=ready pod \
  --selector=app.kubernetes.io/component=controller \
  --timeout=180s

# metrics-server: k3d/k3s ships it; kind does not. Install for kind so the HPA works.
if [ "$RUNTIME" = "kind" ]; then
  echo ">> installing metrics-server (kind has none) — required for the HPA"
  kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml
  # kind nodes use self-signed kubelet certs; let metrics-server trust them (local only).
  kubectl patch deployment metrics-server -n kube-system --type=json \
    -p='[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
fi

# --- 5. apply the PesaCore manifests ---------------------------------------
echo ">> [5/6] applying PesaCore manifests (kustomize)"
kubectl apply -k "$K8S_DIR"

echo ">> waiting for rollouts..."
kubectl -n pesacore rollout status deployment/pesacore-api --timeout=180s
kubectl -n pesacore rollout status deployment/pesacore-web --timeout=180s

# --- 6. done ----------------------------------------------------------------
echo ">> [6/6] cluster up. Resources:"
kubectl -n pesacore get deploy,svc,ingress,hpa
cat <<EOF

==============================================================================
  PesaCore local cluster (${RUNTIME}) is LIVE.

  Access:   http://localhost:8080/
  (host :8080 -> ingress-nginx -> pesacore-web Service -> web pods)

  Inspect:
    kubectl -n pesacore get pods -w           # watch pods
    kubectl -n pesacore get hpa pesacore-api  # watch autoscaling
    kubectl -n pesacore logs deploy/pesacore-web

  Tear down: ./scripts/cluster-down.sh

  NOTE: this is a LOCAL DEMO tier (ADR 0004), not the prod path. Prod = Cloud Run.
==============================================================================
EOF
