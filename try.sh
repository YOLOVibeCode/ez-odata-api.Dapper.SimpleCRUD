#!/usr/bin/env bash
# Try it: clone, run this, get a live API over a seeded database, Swagger UI in your browser and a narrated
# tour of every feature. Needs nothing but bash and curl: if the .NET 10 SDK is missing it is downloaded into
# ./.dotnet (no admin rights, nothing installed system-wide). NuGet packages come from nuget.org on first build.
#
#   ./try.sh                   start the showcase, run the tour, open Swagger UI; Ctrl+C to stop
#   ./try.sh --exit            run the tour and exit with its result (CI)
#   ./try.sh --benchmark       benchmark Dapper, Dapper.SimpleCRUD and EF Core (Docker adds PostgreSQL,
#                              MySQL and SQL Server; without Docker, SQLite only) and write an HTML report
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

PORT=5199
OPEN=1
EXIT=0
BENCHMARK=0
BENCH_ARGS=()
usage() {
  sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
  echo
  echo "Options: --port N  --no-open  --exit  --benchmark [--sqlite-only] [--deep]"
}
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="${2:?--port needs a number}"; shift 2 ;;
    --no-open) OPEN=0; shift ;;
    --exit) EXIT=1; OPEN=0; shift ;;
    --benchmark) BENCHMARK=1; shift ;;
    --sqlite-only|--deep) BENCH_ARGS+=("$1"); shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

has_sdk10() { command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; }

if [[ -x "$ROOT/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$ROOT/.dotnet" PATH="$ROOT/.dotnet:$PATH"
fi
if ! has_sdk10; then
  echo "==> .NET 10 SDK not found; downloading it into ./.dotnet (one time, about 200 MB)"
  command -v curl >/dev/null 2>&1 || { echo "curl is required to download the SDK." >&2; exit 1; }
  for attempt in 1 2 3; do
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$ROOT/.dotnet-install.sh" && break
    [[ $attempt -eq 3 ]] && { echo "Could not download dotnet-install.sh (network?)." >&2; exit 1; }
    sleep $((attempt * 3))
  done
  bash "$ROOT/.dotnet-install.sh" --channel 10.0 --install-dir "$ROOT/.dotnet" --no-path
  rm -f "$ROOT/.dotnet-install.sh"
  export DOTNET_ROOT="$ROOT/.dotnet" PATH="$ROOT/.dotnet:$PATH"
  has_sdk10 || { echo "The .NET 10 SDK did not install correctly." >&2; exit 1; }
fi
echo "==> .NET SDK $(dotnet --version) ($(command -v dotnet))"

if [[ "$BENCHMARK" -eq 1 ]]; then
  flags=(--no-tests --data-access)
  [[ "$OPEN" -eq 0 ]] && flags+=(--no-open)
  exec "$ROOT/compare.sh" "${flags[@]}" "${BENCH_ARGS[@]+"${BENCH_ARGS[@]}"}"
fi

echo "==> Building the showcase (restores packages from nuget.org the first time)"
dotnet build samples/EzOdata.Showcase -c Release -v quiet -nologo

args=(--port "$PORT" --tour)
[[ "$OPEN" -eq 1 ]] && args+=(--open)
[[ "$EXIT" -eq 1 ]] && args+=(--exit)
exec dotnet run --project samples/EzOdata.Showcase -c Release --no-build -- "${args[@]}"
