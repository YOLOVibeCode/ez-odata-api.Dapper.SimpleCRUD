@echo off
rem Double-click or run from cmd: see try.ps1 for options (-Exit, -NoOpen, -Port N, -Benchmark).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0try.ps1" %*
exit /b %ERRORLEVEL%
