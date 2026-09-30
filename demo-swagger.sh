#!/usr/bin/env bash
# Look up Swagger, read shop + warehouse, time both. Uses the sample API.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
PORT="${PORT:-5199}"
BASE="http://127.0.0.1:${PORT}"
ITERATIONS="${1:-20}"
LOG="${TMPDIR:-/tmp}/ezsc-demo-swagger.log"
PID=""

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required on PATH." >&2
  exit 1
fi

cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill "$PID" 2>/dev/null || true
    wait "$PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
echo "==> Starting sample on ${BASE}"
dotnet run --project "$ROOT/samples/EzOdata.SimpleCrud.Sample" --no-launch-profile >"$LOG" 2>&1 &
PID=$!

ready=0
for _ in $(seq 1 80); do
  if curl -sf "$BASE/api/odata/shop/openapi.json" >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$PID" 2>/dev/null; then
    echo "Sample exited before it was ready. Log:" >&2
    cat "$LOG" >&2
    exit 1
  fi
  sleep 0.25
done
if [[ "$ready" -ne 1 ]]; then
  echo "Timed out waiting for ${BASE}. Log:" >&2
  cat "$LOG" >&2
  exit 1
fi

python3 - "$BASE" "$ITERATIONS" <<'PY'
import json, sys, time, urllib.request

base, n = sys.argv[1], int(sys.argv[2])

def get(url):
    with urllib.request.urlopen(url) as r:
        return r.read()

def swagger(prefix, service):
    url = f"{base}{prefix}/{service}/openapi.json"
    doc = json.loads(get(url))
    server = doc["servers"][0]["url"]
    paths = sorted(doc.get("paths", {}))
    return url, server, paths

print()
print("Swagger")
for prefix, service in (
    ("/api/odata", "shop"),
    ("/api/rest", "shop"),
    ("/api/odata", "warehouse"),
    ("/api/rest", "warehouse"),
):
    spec, server, paths = swagger(prefix, service)
    print(f"  {prefix}/{service}")
    print(f"    openapi   {spec}")
    print(f"    server    {server}")
    print(f"    paths     {len(paths)}  (e.g. {', '.join(paths[:4])})")

def names(url):
    data = json.loads(get(url))
    rows = data.get("value", data if isinstance(data, list) else [])
    return [row.get("name") for row in rows]

shop = names(f"{base}/api/odata/shop/products")
wh = names(f"{base}/api/odata/warehouse/products")
print()
print("Read")
print(f"  shop products       {', '.join(shop)}")
print(f"  warehouse products  {', '.join(wh)}")

def time_gets(url, times):
    get(url)  # warmup
    start = time.perf_counter()
    for _ in range(times):
        get(url)
    return (time.perf_counter() - start) * 1000 / times

shop_ms = time_gets(f"{base}/api/odata/shop/products", n)
wh_ms = time_gets(f"{base}/api/odata/warehouse/products", n)
print()
print(f"Timing ({n} GETs after warmup, average)")
print(f"  shop       {shop_ms:.2f} ms")
print(f"  warehouse  {wh_ms:.2f} ms")
print()
PY
