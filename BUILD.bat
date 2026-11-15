@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD.ps1"
if errorlevel 1 (
  echo Build failed. Review the error above.
  pause
  exit /b 1
)
echo Ready: dist\OCS-Link
start "" "%~dp0dist\OCS-Link"
pause
