# Try it on Windows (or PowerShell anywhere): a live API over a seeded database, Swagger UI in your browser
# and a narrated tour. If the .NET 10 SDK is missing it is downloaded into .\.dotnet (no admin rights).
#
#   .\try.cmd                  start the showcase, run the tour, open Swagger UI; Ctrl+C to stop
#   .\try.cmd -Exit            run the tour and exit with its result (CI)
#   .\try.cmd -Benchmark       benchmark Dapper, Dapper.SimpleCRUD and EF Core and write an HTML report
#                              (Docker adds PostgreSQL, MySQL and SQL Server; without it, SQLite only)
param(
    [int]$Port = 5199,
    [switch]$NoOpen,
    [switch]$Exit,
    [switch]$Benchmark,
    [switch]$SqliteOnly
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Test-Sdk10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    return [bool](& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' })
}

$local = Join-Path $root '.dotnet'
if (Test-Path (Join-Path $local 'dotnet*')) {
    $env:DOTNET_ROOT = $local
    $env:PATH = "$local$([IO.Path]::PathSeparator)$env:PATH"
}
if (-not (Test-Sdk10)) {
    Write-Host '==> .NET 10 SDK not found; downloading it into .\.dotnet (one time, about 200 MB)'
    $installer = Join-Path $root '.dotnet-install.ps1'
    foreach ($attempt in 1..3) {
        try { Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile $installer; break }
        catch { if ($attempt -eq 3) { throw } ; Start-Sleep -Seconds (3 * $attempt) }
    }
    & $installer -Channel 10.0 -InstallDir $local -NoPath
    Remove-Item $installer -Force
    $env:DOTNET_ROOT = $local
    $env:PATH = "$local$([IO.Path]::PathSeparator)$env:PATH"
    if (-not (Test-Sdk10)) { throw 'The .NET 10 SDK did not install correctly.' }
}
Write-Host "==> .NET SDK $(dotnet --version)"

if ($Benchmark) {
    # One database per process, like compare.sh: SQLite, then PostgreSQL, MySQL and SQL Server when Docker runs.
    $out = Join-Path $root "artifacts/compare/$(Get-Date -Format yyyyMMdd-HHmmss)"
    $kinds = @('sqlite')
    if (-not $SqliteOnly -and (Get-Command docker -ErrorAction SilentlyContinue)) {
        & docker info *> $null
        if ($LASTEXITCODE -eq 0) { $kinds += 'postgresql', 'mysql', 'sqlserver' }
    }
    & dotnet build benchmarks/EzOdata.Entities.Benchmarks -c Release -v quiet -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $status = 0
    foreach ($kind in $kinds) {
        Write-Host "==> Benchmarks: $kind"
        & dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release --no-build -- --all --only $kind --out (Join-Path $out $kind)
        if ($LASTEXITCODE -ne 0) { $status = $LASTEXITCODE }
    }
    & dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release --no-build -- report --tests (Join-Path $out 'tests') --runs $out --out $out
    Write-Host "Report: $(Join-Path $out 'report.html')"
    if (-not $NoOpen) { Start-Process (Join-Path $out 'report.html') }
    exit $status
}

Write-Host '==> Building the showcase (restores packages from nuget.org the first time)'
& dotnet build samples/EzOdata.Showcase -c Release -v quiet -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$runArgs = @('--port', $Port, '--tour')
if (-not $NoOpen -and -not $Exit) { $runArgs += '--open' }
if ($Exit) { $runArgs += '--exit' }
& dotnet run --project samples/EzOdata.Showcase -c Release --no-build -- @runArgs
exit $LASTEXITCODE
