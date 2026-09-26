#!/usr/bin/env bash
#
# Retakes the README screenshots in docs/screenshots/.
#
#   scripts/screenshots.sh
#
# Starts a throwaway instance with its own data folder, deploys the sample sites and
# functions through the UI, and captures each screen at the same size every time. Nothing
# touches your real data or a running container.
#
# Needs the .NET SDK, Node 18+ and zip. The first run downloads playwright-core and a
# Chromium build (about 150 MB, cached in ~/.cache/ms-playwright), neither of which the app
# itself uses. If optipng is installed the PNGs are also shrunk.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
port="${SCREENSHOT_PORT:-18095}"
out="$root/docs/screenshots"
data="$(mktemp -d)"
server=""

cleanup() {
  [[ -n "$server" ]] && kill "$server" 2>/dev/null && wait "$server" 2>/dev/null
  rm -rf "$data"
}
trap cleanup EXIT

for tool in dotnet node npm zip; do
  command -v "$tool" >/dev/null || { echo "$tool is required." >&2; exit 1; }
done

if curl -s -o /dev/null "http://localhost:$port/healthz"; then
  echo "Port $port is already in use. Set SCREENSHOT_PORT to another port." >&2
  exit 1
fi

echo "Building the sample zips and the app…"
"$root/scripts/build-samples.sh" >/dev/null
dotnet build "$root/src/StaticSiteHost" -v quiet -nologo >/dev/null

echo "Preparing the browser…"
(cd "$root/scripts/screenshots" && npm install --silent --no-audit --no-fund)
(cd "$root/scripts/screenshots" && npx --no-install playwright-core install chromium >/dev/null)

echo "Starting a throwaway instance on port $port…"
ASPNETCORE_ENVIRONMENT=Development \
SiteHosting__DataRoot="$data" \
ASPNETCORE_URLS="http://localhost:$port" \
  dotnet run --no-build --no-launch-profile --project "$root/src/StaticSiteHost" > "$data/server.log" 2>&1 &
server=$!

for _ in $(seq 1 60); do
  curl -s -o /dev/null "http://localhost:$port/healthz" && break
  sleep 1
done
curl -s -o /dev/null "http://localhost:$port/healthz" || { echo "The app did not start:" >&2; tail -20 "$data/server.log" >&2; exit 1; }

echo "Capturing into docs/screenshots/…"
mkdir -p "$out"
if ! BASE_URL="http://localhost:$port" DATA_ROOT="$data" REPO="$root" OUT_DIR="$out" \
     node "$root/scripts/screenshots/capture.mjs"; then
  echo "Capturing failed. The last lines of the server log:" >&2
  tail -20 "$data/server.log" >&2
  exit 1
fi

if command -v optipng >/dev/null; then
  optipng -quiet -o2 "$out"/*.png
fi

du -ch "$out"/*.png | tail -1 | sed 's/total$/in docs\/screenshots/'
