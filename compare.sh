#!/usr/bin/env bash
# One-click: build, test, benchmark, and write a side-by-side report (report.html + report.md).
#
#   ./compare.sh                     tests + HTTP timings for stock ez-odata, SimpleCRUD and EF Core engines
#   ./compare.sh --data-access       also the libraries on their own: Dapper vs Dapper.SimpleCRUD vs EF Core
#                                    (BenchmarkDotNet, 20 measured iterations per case)
#   ./compare.sh --quick             shorter runs (ShortRun, 50 HTTP iterations) for a fast look
#   ./compare.sh --sqlite-only       no Docker
#   ./compare.sh --deep              HTTP through BenchmarkDotNet instead of the interleaved harness
#
# Each database is benchmarked in its own process, alone in the Docker VM: SQLite, then PostgreSQL, MySQL and
# SQL Server (Azure SQL Edge on ARM). Test projects run one after another (they share Docker).
set -u
set -o pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

DEEP=0
SQLITE_ONLY=0
ITERATIONS=200
NO_TESTS=0
NO_OPEN=0
DATA_ACCESS=0
QUICK=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --deep) DEEP=1; shift ;;
    --sqlite-only) SQLITE_ONLY=1; shift ;;
    --iterations) ITERATIONS="${2:-200}"; shift 2 ;;
    --no-tests) NO_TESTS=1; shift ;;
    --no-open) NO_OPEN=1; shift ;;
    --data-access) DATA_ACCESS=1; shift ;;
    --quick) QUICK=1; ITERATIONS=50; shift ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown flag: $1" >&2; exit 2 ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required on PATH (./try.sh installs it locally if you have none)." >&2
  exit 1
fi

KINDS=(sqlite)
if [[ "$SQLITE_ONLY" -eq 1 ]]; then
  export EZSC_SKIP_DOCKER=1
elif ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  export EZSC_SKIP_DOCKER=1
  echo "Docker is not available; running SQLite only."
else
  KINDS+=(postgresql mysql sqlserver)
fi

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$ROOT/artifacts/compare/$STAMP"
mkdir -p "$OUT/tests"

echo "==> Build (Release)"
dotnet build EzOdata.SimpleCrud.slnx -c Release -nologo -v quiet || { echo "Build failed." >&2; exit 1; }

TEST_STATUS=0
if [[ "$NO_TESTS" -eq 0 ]]; then
  for project in tests/EzOdata.SimpleCrud.Tests tests/EzOdata.Entities.AspNetCore.Tests; do
    echo "==> Tests: $project"
    dotnet test "$project" -c Release --no-build --logger trx --results-directory "$OUT/tests" || TEST_STATUS=$?
  done
fi

BENCH_STATUS=0
for kind in "${KINDS[@]}"; do
  echo "==> Benchmarks: $kind (full log: $OUT/$kind.log)"
  args=(--only "$kind" --out "$OUT/$kind" --iterations "$ITERATIONS")
  [[ "$DATA_ACCESS" -eq 1 ]] && args+=(--all)
  [[ "$QUICK" -eq 1 ]] && args+=(--quick)
  [[ "$DEEP" -eq 1 ]] && args+=(--deep)
  dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release --no-build -- "${args[@]}" 2>&1 \
    | tee "$OUT/$kind.log" \
    | grep --line-buffered -E "^(Databases|Verified|MISMATCH|Retrying|Skipping|Engines|Wrote|  [a-z]+ +[a-z0-9-]+ +[a-z]+$|// Benchmark: |Unhandled)"
  status=${PIPESTATUS[0]}
  if [[ $status -ne 0 ]]; then
    echo "  $kind failed (exit $status); see $OUT/$kind.log" >&2
    BENCH_STATUS=$status
  fi
done

echo "==> Report"
dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release --no-build -- \
  report --tests "$OUT/tests" --runs "$OUT" --out "$OUT"

if [[ "$NO_OPEN" -eq 0 && -f "$OUT/report.html" ]]; then
  if command -v open >/dev/null 2>&1; then
    open "$OUT/report.html"
  elif command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$OUT/report.html" >/dev/null 2>&1 || true
  fi
fi

echo "Report: $OUT/report.html"
if [[ $TEST_STATUS -ne 0 ]]; then
  echo "One or more tests failed (exit $TEST_STATUS)." >&2
  exit $TEST_STATUS
fi
if [[ $BENCH_STATUS -ne 0 ]]; then
  echo "Benchmarks failed (exit $BENCH_STATUS)." >&2
  exit $BENCH_STATUS
fi
exit 0
