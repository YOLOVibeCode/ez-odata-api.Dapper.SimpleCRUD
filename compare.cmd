@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set DEEP=0
set SQLITE_ONLY=0
set ITERATIONS=200
set NO_TESTS=0
set NO_OPEN=0

:parse
if "%~1"=="" goto parsed
if /I "%~1"=="--deep" set DEEP=1& shift& goto parse
if /I "%~1"=="--sqlite-only" set SQLITE_ONLY=1& shift& goto parse
if /I "%~1"=="--iterations" set ITERATIONS=%~2& shift& shift& goto parse
if /I "%~1"=="--no-tests" set NO_TESTS=1& shift& goto parse
if /I "%~1"=="--no-open" set NO_OPEN=1& shift& goto parse
echo Unknown flag: %~1
exit /b 2

:parsed
where dotnet >nul 2>&1
if errorlevel 1 (
  echo dotnet is required on PATH.
  exit /b 1
)

where docker >nul 2>&1
if errorlevel 1 set EZSC_SKIP_DOCKER=1
if "%SQLITE_ONLY%"=="1" set EZSC_SKIP_DOCKER=1

for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set STAMP=%%i
set OUT=%cd%\artifacts\compare\%STAMP%
mkdir "%OUT%\tests" >nul 2>&1
mkdir "%OUT%\bench" >nul 2>&1

echo ==^> Build (Release)
dotnet build EzOdata.SimpleCrud.slnx -c Release
if errorlevel 1 exit /b 1

set TEST_STATUS=0
if "%NO_TESTS%"=="0" (
  echo ==^> Tests
  dotnet test EzOdata.SimpleCrud.slnx -c Release --no-build --logger trx --results-directory "%OUT%\tests"
  set TEST_STATUS=%ERRORLEVEL%
)

set BENCH_ARGS=--out "%OUT%" --iterations %ITERATIONS%
if "%DEEP%"=="1" set BENCH_ARGS=%BENCH_ARGS% --deep
if defined EZSC_SKIP_DOCKER set BENCH_ARGS=%BENCH_ARGS% --sqlite-only

echo ==^> Benchmarks
dotnet run --project benchmarks\EzOdata.Entities.Benchmarks -c Release --no-build -- %BENCH_ARGS%
set BENCH_STATUS=%ERRORLEVEL%

echo ==^> Report
set REPORT_ARGS=report --tests "%OUT%\tests" --out "%OUT%"
if exist "%OUT%\bench\quick.json" set REPORT_ARGS=%REPORT_ARGS% --bench "%OUT%\bench\quick.json"
dotnet run --project benchmarks\EzOdata.Entities.Benchmarks -c Release --no-build -- %REPORT_ARGS%

if "%NO_OPEN%"=="0" if exist "%OUT%\report.html" start "" "%OUT%\report.html"

echo Report: %OUT%\report.html
if not "%TEST_STATUS%"=="0" exit /b %TEST_STATUS%
if not "%BENCH_STATUS%"=="0" exit /b %BENCH_STATUS%
exit /b 0
