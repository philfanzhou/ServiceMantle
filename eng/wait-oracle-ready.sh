#!/usr/bin/env bash
set -euo pipefail

container="${1:?Oracle container name is required.}"
attempts="${2:-90}"
delay="${3:-10}"
if [[ ! "$attempts" =~ ^[1-9][0-9]*$ || ! "$delay" =~ ^[0-9]+$ ]]; then
  echo "Oracle readiness polling arguments are invalid." >&2
  exit 1
fi

# The pinned lite image opens its PDB before setting ORACLE_PWD. Its local-auth
# healthcheck can succeed in that interval; the startup marker follows setPassword.sh.
for ((attempt = 1; attempt <= attempts; attempt++)); do
  if ! state="$(docker inspect --format '{{.State.Status}}' "$container" 2>/dev/null)" ||
     ! health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container" 2>/dev/null)"; then
    echo "Oracle readiness failed: container inspection failed." >&2
    exit 1
  fi
  if [[ "$state" == "exited" || "$state" == "dead" ]]; then
    echo "Oracle readiness failed: container stopped before initialization completed." >&2
    exit 1
  fi
  # Consume the full stream without printing it: raw startup logs may contain credentials.
  if ! initialized="$(docker logs "$container" 2>&1 | awk '/^DATABASE IS READY TO USE!$/ {ready=1} END {print ready+0}')"; then
    echo "Oracle readiness failed: startup state could not be read." >&2
    exit 1
  fi
  if [[ "$health" == "healthy" && "$initialized" == "1" ]]; then
    echo "Oracle initialization completed and healthcheck passed; verifying listener credentials next."
    exit 0
  fi
  if ((attempt < attempts)); then sleep "$delay"; fi
done

echo "Oracle readiness timed out: healthy=$([[ "$health" == "healthy" ]] && echo yes || echo no), initialized=$initialized." >&2
exit 1
