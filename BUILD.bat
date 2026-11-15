@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD.ps1" -InstallSdk
if errorlevel 1 (
  echo Build failed. See the stage above and BUILD.log in this folder.
  pause
  exit /b 1
)
if not exist "%~dp0dist\OCS-Link\host\OCS-Host.exe" (
  echo Host output is missing. See BUILD.log in this folder.
  pause
  exit /b 1
)
echo Ready: %~dp0dist\OCS-Link
start "" "%~dp0dist\OCS-Link"
pause
