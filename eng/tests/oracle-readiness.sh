#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
cat > "$fixture/docker" <<'MOCK'
#!/usr/bin/env bash
case "$1" in
  inspect)
    if [[ "$3" == '{{.State.Status}}' ]]; then
      case "$SCENARIO" in
        exited) echo exited ;;
        inspect-failure) echo 'credential-sentinel' >&2; exit 1 ;;
        *) echo running ;;
      esac
    elif [[ "$SCENARIO" == 'unhealthy' ]]; then echo starting
    else echo healthy; fi ;;
  logs)
    echo 'credential-sentinel'
    case "$SCENARIO" in
      delayed)
        if [[ -f "$MOCK_STATE" ]]; then echo 'DATABASE IS READY TO USE!'; fi
        touch "$MOCK_STATE" ;;
      ready|unhealthy) echo 'DATABASE IS READY TO USE!' ;;
      log-failure) exit 1 ;;
    esac ;;
  *) exit 1 ;;
esac
MOCK
chmod +x "$fixture/docker"
export PATH="$fixture:$PATH" MOCK_STATE="$fixture/state"
for scenario in ready delayed never-ready unhealthy exited inspect-failure log-failure; do
  rm -f "$MOCK_STATE"
  export SCENARIO="$scenario"
  code=0
  output="$(bash "$root/eng/wait-oracle-ready.sh" test-oracle 2 0 2>&1)" || code=$?
  if [[ "$output" == *credential-sentinel* ]]; then
    echo "Readiness exposed raw container output." >&2; exit 1
  fi
  case "$scenario" in
    ready|delayed) [[ "$code" == 0 ]] ;;
    never-ready) [[ "$code" != 0 && "$output" == *'healthy=yes, initialized=0'* ]] ;;
    unhealthy) [[ "$code" != 0 && "$output" == *'healthy=no, initialized=1'* ]] ;;
    *) [[ "$code" != 0 && "$output" == *'Oracle readiness failed:'* ]] ;;
  esac
  echo "Passed: $scenario"
done
