#!/usr/bin/env bash
# Builds a consumer project against packed ServiceMantle artifacts only.
#
# The consumer resolves ServiceMantle packages from a local folder feed and an isolated NuGet cache,
# so a passing run proves the shipped package works on its own: no ProjectReference, no sibling
# repository path, and no ServiceMantle package left in a shared cache from an earlier build.
#
# Usage: package-consumption.sh --version V --packages DIR --consumer DIR
set -euo pipefail

version=""
packages=""
consumer=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="${2:-}"; shift 2 ;;
    --packages) packages="${2:-}"; shift 2 ;;
    --consumer) consumer="${2:-}"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$version" || -z "$packages" || -z "$consumer" ]]; then
  echo "Usage: package-consumption.sh --version V --packages DIR --consumer DIR" >&2
  exit 2
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
packages="$(cd "$packages" && pwd)"
consumer="$(cd "$consumer" && pwd)"

workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

# A private cache is what makes the feed restriction real. Without it the restore could silently
# succeed from a package the repository build had already placed in the shared global cache.
export NUGET_PACKAGES="$workspace/nuget-cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=true

project="$workspace/consumer"
mkdir -p "$project"
cp "$consumer"/* "$project/"
# The repository-wide Directory.Build.props and Directory.Packages.props must not reach the
# consumer; it has to stand on the package metadata alone.
printf '<Project />\n' > "$project/Directory.Build.props"
printf '<Project />\n' > "$project/Directory.Packages.props"

cat > "$project/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="servicemantle-local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="servicemantle-local">
      <package pattern="ServiceMantle*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

for file in "$project"/*.csproj; do
  sed -i.bak "s/__SERVICEMANTLE_VERSION__/$version/g" "$file"
  rm -f "$file.bak"
done

echo "Restoring consumer against $packages with an isolated cache."
dotnet restore "$project" --configfile "$project/nuget.config"
dotnet build "$project" --configuration Release --no-restore

# Every ServiceMantle assembly the consumer resolved has to come from the local feed, and no
# retired package id may reappear through a transitive dependency.
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
  id="${library%%/*}"
  if [[ ! -f "$packages/$id.$version.nupkg" ]]; then
    echo "Consumer resolved $library, which the local feed does not provide." >&2
    exit 1
  fi
done <<< "$resolved"

echo "Starting the consumer against the packaged assemblies."
dotnet run --project "$project" --configuration Release --no-build --no-restore

echo "Package consumption verified for version $version."
