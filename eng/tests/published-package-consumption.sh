#!/usr/bin/env bash
# Builds and runs a consumer against packages restored from a remote NuGet source.
#
# This is the only check that speaks for a consumer rather than for the build that produced the
# package: the artifacts come back from the feed, through a cache that has never held a ServiceMantle
# package, so nothing on the runner can make a broken or missing package look installable.
#
# Usage: published-package-consumption.sh --version V --source URL --consumer DIR [--attempts N]
set -euo pipefail

version=""
source_url=""
consumer=""
attempts=10
delay_seconds=30

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="${2:-}"; shift 2 ;;
    --source) source_url="${2:-}"; shift 2 ;;
    --consumer) consumer="${2:-}"; shift 2 ;;
    --attempts) attempts="${2:-}"; shift 2 ;;
    --delay-seconds) delay_seconds="${2:-}"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$version" || -z "$source_url" || -z "$consumer" ]]; then
  echo "Usage: published-package-consumption.sh --version V --source URL --consumer DIR" >&2
  exit 2
fi

if [[ ! "$attempts" =~ ^[0-9]+$ ]] || (( attempts < 1 )); then
  echo "The attempt budget must be a positive integer." >&2
  exit 2
fi

consumer="$(cd "$consumer" && pwd)"

workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

export NUGET_PACKAGES="$workspace/nuget-cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=true

project="$workspace/consumer"
mkdir -p "$project"
cp "$consumer"/* "$project/"
printf '<Project />\n' > "$project/Directory.Build.props"
printf '<Project />\n' > "$project/Directory.Packages.props"

cat > "$project/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="release" value="$source_url" />
  </packageSources>
</configuration>
EOF

for file in "$project"/*.csproj; do
  sed -i.bak "s/__SERVICEMANTLE_VERSION__/$version/g" "$file"
  rm -f "$file.bak"
done

# A push is visible to the feed's search and restore paths some time after it is accepted, so a
# first failure here is expected rather than a defect. The budget is finite: an indefinite wait
# would turn "never indexed" into a job that hangs until its timeout instead of reporting failure.
restored=false
for attempt in $(seq 1 "$attempts"); do
  echo "Restore attempt $attempt of $attempts for version $version."
  if dotnet restore "$project" --configfile "$project/nuget.config"; then
    restored=true
    break
  fi

  if (( attempt < attempts )); then
    echo "Version $version is not restorable yet; waiting ${delay_seconds}s."
    sleep "$delay_seconds"
  fi
done

if [[ "$restored" != "true" ]]; then
  echo "Version $version did not become restorable from $source_url within $attempts attempts." >&2
  exit 1
fi

dotnet build "$project" --configuration Release --no-restore

resolved="$(python3 - "$project" <<'PY'
import json, pathlib, sys
assets = json.loads((pathlib.Path(sys.argv[1]) / "obj" / "project.assets.json").read_text())
print("\n".join(sorted(
    name for name in assets["libraries"] if name.startswith("ServiceMantle"))))
PY
)"
echo "Resolved ServiceMantle libraries:"
echo "$resolved" | sed 's/^/  /'
while IFS= read -r library; do
  [[ -n "$library" ]] || continue
  if [[ "${library##*/}" != "$version" ]]; then
    echo "Consumer resolved $library instead of version $version." >&2
    exit 1
  fi
done <<< "$resolved"

echo "Starting the consumer against the published packages."
dotnet run --project "$project" --configuration Release --no-build --no-restore

echo "Published package consumption verified for version $version from $source_url."
