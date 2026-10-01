#!/usr/bin/env bash
# Drop-in analysis: what it costs to add the instant API to a brand-new ASP.NET Core project that already has a
# database, using only the published packages from nuget.org.
#
#   benchmarks/DropIn/run.sh
#
# For each variant (stock ez-odata, + the SimpleCRUD engine, + the EF Core engine) it creates a fresh project with
# `dotnet new web` outside this repository (so none of this repo's build settings apply), copies in the few files a
# user writes (apps/<variant>), adds one package with an empty NuGet cache, publishes in Release and measures:
# code written, packages and bytes downloaded, publish size, cold start to the first query, memory, assemblies and
# load contexts, and HTTP latency and throughput over real sockets (all three apps interleaved).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${WORK:-${TMPDIR:-/tmp}/ezodata-dropin}"
OUT="${OUT:-$REPO/artifacts/drop-in/$(date +%Y%m%d-%H%M%S)}"
REQUESTS="${REQUESTS:-6000}"       # per scenario and app, one request at a time
REQUESTS16="${REQUESTS16:-24000}"  # per scenario and app, 16 at a time
VARIANTS=(stock simplecrud efcore)
package() { case $1 in stock) echo "EzOdata.AspNetCore 1.0.7" ;; simplecrud) echo "EzOdata.SimpleCrud.AspNetCore 2.0.1" ;; efcore) echo "EzOdata.EntityFrameworkCore.AspNetCore 2.0.1" ;; esac; }
port() { case $1 in stock) echo 5701 ;; simplecrud) echo 5702 ;; efcore) echo 5703 ;; esac; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

rm -rf "$WORK"; mkdir -p "$WORK" "$OUT"
for v in "${VARIANTS[@]}"; do # a port still held from an interrupted run would make the app exit at once
  lsof -nP -iTCP:"$(port $v)" -sTCP:LISTEN -t 2>/dev/null | xargs kill 2>/dev/null || true
done
TOOL="$HERE/tool/bin/Release/net10.0/DropIn.Tool.dll"
dotnet build "$HERE/tool" -c Release -nologo -v quiet > /dev/null

echo "==> Existing data: the showcase's shop database (500 customers, orders, order lines)"
dotnet run --project "$REPO/samples/EzOdata.Showcase" -c Release -- --data "$WORK/data" --port 5698 --exit > /dev/null

ms() { python3 -c 'import time; print(int(time.time()*1000))'; }
loc() { # lines a user writes: not blank, not using, not comment-only, not the diagnostics line
  cat "$@" | grep -vE '^\s*$|^\s*using |^\s*//|DropInDiagnostics' | wc -l | tr -d ' '
}

for v in "${VARIANTS[@]}"; do
  echo "==> $v: $(package $v)"
  dir="$WORK/$v"
  mkdir -p "$WORK/db-$v" && cp "$WORK/data/simplecrud.db" "$WORK/db-$v/shop.db"
  dotnet new web -o "$dir" -n DropIn --framework net10.0 --no-restore > /dev/null
  cp "$HERE/apps/$v/"*.cs "$dir/"
  cp "$HERE/apps/shared/DropInDiagnostics.cs" "$dir/"
  python3 - "$dir/appsettings.json" "$WORK/db-$v/shop.db" <<'PY'
import json, sys
path, db = sys.argv[1], sys.argv[2]
settings = json.load(open(path))
settings["ConnectionStrings"] = {"shop": db}
json.dump(settings, open(path, "w"), indent=2)
PY

  # Cold: an empty package folder and HTTP cache, as on a fresh machine.
  export NUGET_PACKAGES="$WORK/nuget/$v" NUGET_HTTP_CACHE_PATH="$WORK/http-cache/$v"
  read -r id version <<< "$(package $v)"
  t0=$(ms); dotnet add "$dir" package "$id" --version "$version" > "$OUT/$v-add.log" 2>&1; t1=$(ms)
  dotnet list "$dir" package --include-transitive --format json | sed "s#$WORK/#<work>/#g" > "$OUT/$v-packages.json"
  t2=$(ms); dotnet publish "$dir" -c Release -o "$dir/pub" -nologo -v quiet > "$OUT/$v-publish.log" 2>&1; t3=$(ms)
  unset NUGET_PACKAGES NUGET_HTTP_CACHE_PATH

  python3 - "$OUT/$v-build.json" <<PY
import json, os, sys
pub = "$dir/pub"
files = [os.path.join(r, f) for r, _, fs in os.walk(pub) for f in fs]
pk = json.load(open("$OUT/$v-packages.json"))
fw = pk["projects"][0]["frameworks"][0]
json.dump({
  "package": "$(package $v)",
  "linesWritten": int("$(loc "$dir"/Program.cs $(ls "$dir"/*.cs | grep -v -e Program.cs -e DropInDiagnostics.cs))"),
  "files": sorted(f for f in os.listdir("$HERE/apps/$v")),
  "addPackageSeconds": ($t1 - $t0) / 1000, "publishSeconds": ($t3 - $t2) / 1000,
  "downloadedMb": round(sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk("$WORK/nuget/$v") for f in fs if f.endswith(".nupkg")) / 1048576, 1),
  "topLevelPackages": [p["id"] + " " + p["resolvedVersion"] for p in fw.get("topLevelPackages", [])],
  "transitivePackages": len(fw.get("transitivePackages", [])),
  "publishMb": round(sum(os.path.getsize(f) for f in files) / 1048576, 1),
  "publishDlls": sum(1 for f in files if f.endswith(".dll")),
}, open(sys.argv[1], "w"), indent=2)
PY
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print("    %s lines · %s transitive packages · %s MB downloaded · publish %s MB, %s DLLs" % (d["linesWritten"], d["transitivePackages"], d["downloadedMb"], d["publishMb"], d["publishDlls"]))' "$OUT/$v-build.json"

done

echo "==> Cold start: process start to the first query, 21 rounds, the three apps in rotating order"
dotnet "$TOOL" startup-all --runs 21 --env ASPNETCORE_ENVIRONMENT=Development \
  $(for v in "${VARIANTS[@]}"; do echo "--app $v=$WORK/$v/pub/DropIn.dll=http://127.0.0.1:$(port $v)"; done) > "$OUT/startup.json"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); [print("    %-11s fastest %.0f ms, median %.0f ms" % (k, min(v["runs"]), v["medianMs"])) for k, v in d.items()]' "$OUT/startup.json"

echo "==> HTTP load: all three running, interleaved"
pids=()
for v in "${VARIANTS[@]}"; do
  (cd "$WORK/$v/pub" && ASPNETCORE_ENVIRONMENT=Development exec dotnet DropIn.dll --urls "http://127.0.0.1:$(port $v)") > "$OUT/$v-run.log" 2>&1 &
  pids+=($!)
done
trap 'kill "${pids[@]}" 2>/dev/null || true' EXIT
for v in "${VARIANTS[@]}"; do
  until curl -sf "http://127.0.0.1:$(port $v)/api/odata/shop/customers?\$top=1" > /dev/null; do sleep 0.2; done
done
dotnet "$TOOL" load --requests "$REQUESTS" --requests16 "$REQUESTS16" --rounds 6 --out "$OUT/load.json" \
  $(for v in "${VARIANTS[@]}"; do echo "--target $v=http://127.0.0.1:$(port $v)"; done)
for v in "${VARIANTS[@]}"; do curl -s "http://127.0.0.1:$(port $v)/_diag" > "$OUT/$v-after-load.json"; done
kill "${pids[@]}" 2>/dev/null || true

cp -R "$HERE/apps" "$OUT/apps"
echo "Results: $OUT"
