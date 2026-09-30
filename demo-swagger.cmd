@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set PORT=5199
if not "%~1"=="" (set ITERATIONS=%~1) else set ITERATIONS=20
set BASE=http://127.0.0.1:%PORT%
set LOG=%TEMP%\ezsc-demo-swagger.log

where dotnet >nul 2>&1
if errorlevel 1 (
  echo dotnet is required on PATH.
  exit /b 1
)

if not defined ASPNETCORE_ENVIRONMENT set ASPNETCORE_ENVIRONMENT=Development
echo ==^> Starting sample on %BASE%
for /f %%p in ('powershell -NoProfile -Command "(Start-Process -FilePath dotnet -ArgumentList 'run','--project','samples\EzOdata.SimpleCrud.Sample','--no-launch-profile' -RedirectStandardOutput '%LOG%' -RedirectStandardError '%LOG%' -PassThru -NoNewWindow).Id"') do set PID=%%p

set READY=0
for /L %%i in (1,1,80) do (
  curl -sf "%BASE%/api/odata/shop/openapi.json" >nul 2>&1
  if not errorlevel 1 (
    set READY=1
    goto :ready
  )
  timeout /t 1 /nobreak >nul
)
:ready
if not "%READY%"=="1" (
  echo Timed out waiting for %BASE%. Log:
  type "%LOG%"
  if defined PID taskkill /PID %PID% /T /F >nul 2>&1
  exit /b 1
)

powershell -NoProfile -Command ^
  "$base='%BASE%'; $n=%ITERATIONS%;" ^
  "Write-Host ''; Write-Host 'Swagger';" ^
  "foreach ($pair in @(@('/api/odata','shop'),@('/api/rest','shop'),@('/api/odata','warehouse'),@('/api/rest','warehouse'))) {" ^
  "  $prefix=$pair[0]; $svc=$pair[1]; $spec = $base + $prefix + '/' + $svc + '/openapi.json';" ^
  "  $doc=Invoke-RestMethod $spec; $paths=@($doc.paths.PSObject.Properties.Name);" ^
  "  Write-Host ('  {0}/{1}' -f $prefix, $svc);" ^
  "  Write-Host ('    openapi   {0}' -f $spec);" ^
  "  Write-Host ('    server    {0}' -f $doc.servers[0].url);" ^
  "  Write-Host ('    paths     {0}  (e.g. {1})' -f $paths.Count, (($paths | Select-Object -First 4) -join ', '))" ^
  "}" ^
  "function Names($u){ @((Invoke-RestMethod $u).value | ForEach-Object { $_.name }) }" ^
  "$shop=Names ($base + '/api/odata/shop/products'); $wh=Names ($base + '/api/odata/warehouse/products');" ^
  "Write-Host ''; Write-Host 'Read';" ^
  "Write-Host ('  shop products       {0}' -f ($shop -join ', '));" ^
  "Write-Host ('  warehouse products  {0}' -f ($wh -join ', '));" ^
  "function Time-Gets($u,$times){ Invoke-RestMethod $u | Out-Null; $sw=[Diagnostics.Stopwatch]::StartNew(); 1..$times | ForEach-Object { Invoke-RestMethod $u | Out-Null }; $sw.Stop(); $sw.Elapsed.TotalMilliseconds / $times }" ^
  "$shopMs=Time-Gets ($base + '/api/odata/shop/products') $n;" ^
  "$whMs=Time-Gets ($base + '/api/odata/warehouse/products') $n;" ^
  "Write-Host ''; Write-Host ('Timing ({0} GETs after warmup, average)' -f $n);" ^
  "Write-Host ('  shop       {0:n2} ms' -f $shopMs);" ^
  "Write-Host ('  warehouse  {0:n2} ms' -f $whMs); Write-Host ''"

if defined PID taskkill /PID %PID% /T /F >nul 2>&1
exit /b 0
