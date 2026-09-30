#!/usr/bin/env bash
# One-click: build, test both engines, time them, write a side-by-side report.
set -u
set -o pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

DEEP=0
SQLITE_ONLY=0
ITERATIONS=200
NO_TESTS=0
NO_OPEN=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --deep) DEEP=1; shift ;;
    --sqlite-only) SQLITE_ONLY=1; shift ;;
    --iterations) ITERATIONS="${2:-200}"; shift 2 ;;
    --no-tests) NO_TESTS=1; shift ;;
    --no-open) NO_OPEN=1; shift ;;
    -h|--help)
      echo "Usage: ./compare.sh [--deep] [--sqlite-only] [--iterations N] [--no-tests] [--no-open]"
      exit 0
      ;;
    *) echo "Unknown flag: $1" >&2; exit 2 ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required on PATH." >&2
  exit 1
fi

if [[ "$SQLITE_ONLY" -eq 1 ]] || ! command -v docker >/dev/null 2>&1; then
  export EZSC_SKIP_DOCKER=1
  if [[ "$SQLITE_ONLY" -eq 0 ]]; then
    echo "Docker not found; running SQLite only (EZSC_SKIP_DOCKER=1)."
  fi
fi

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$ROOT/artifacts/compare/$STAMP"
mkdir -p "$OUT/tests" "$OUT/bench"

echo "==> Build (Release)"
dotnet build EzOdata.SimpleCrud.slnx -c Release
BUILD_STATUS=$?
if [[ $BUILD_STATUS -ne 0 ]]; then
  echo "Build failed." >&2
  exit $BUILD_STATUS
fi

TEST_STATUS=0
if [[ "$NO_TESTS" -eq 0 ]]; then
  echo "==> Tests"
  set +e
  dotnet test EzOdata.SimpleCrud.slnx -c Release --no-build --logger trx --results-directory "$OUT/tests"
  TEST_STATUS=$?
  set -e
fi

echo "==> Benchmarks"
BENCH_ARGS=(--out "$OUT" --iterations "$ITERATIONS")
[[ "$DEEP" -eq 1 ]] && BENCH_ARGS+=(--deep)
[[ "$SQLITE_ONLY" -eq 1 || "${EZSC_SKIP_DOCKER:-}" == "1" ]] && BENCH_ARGS+=(--sqlite-only)
dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release --no-build -- "${BENCH_ARGS[@]}"
BENCH_STATUS=$?

echo "==> Report"
REPORT_ARGS=(report --tests "$OUT/tests" --out "$OUT")
if [[ -f "$OUT/bench/quick.json" ]]; then
  REPORT_ARGS+=(--bench "$OUT/bench/quick.json")
fi
dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release --no-build -- "${REPORT_ARGS[@]}"

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
