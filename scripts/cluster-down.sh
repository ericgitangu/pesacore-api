#!/usr/bin/env bash
# PesaCore — Tier C: delete the LOCAL k8s cluster (throwaway demo infra, ADR 0004).
# Deleting the whole cluster is cleaner + faster than `kubectl delete -k` and
# leaves nothing behind. Tries k3d first, then kind.
#
#   ./scripts/cluster-down.sh
set -euo pipefail

CLUSTER="pesacore"
DELETED=0

if command -v k3d >/dev/null 2>&1 && k3d cluster list 2>/dev/null | grep -qw "$CLUSTER"; then
  echo ">> deleting k3d cluster '${CLUSTER}'"
  k3d cluster delete "$CLUSTER"
  DELETED=1
fi

if command -v kind >/dev/null 2>&1 && kind get clusters 2>/dev/null | grep -qw "$CLUSTER"; then
  echo ">> deleting kind cluster '${CLUSTER}'"
  kind delete cluster --name "$CLUSTER"
  DELETED=1
fi

if [ "$DELETED" -eq 0 ]; then
  echo ">> no '${CLUSTER}' cluster found (k3d or kind). Nothing to do."
else
  echo ">> done — cluster removed."
fi
