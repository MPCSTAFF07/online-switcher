@echo off
setlocal
cd /d "%~dp0"
if not exist "host\OCS-Host.exe" (
  echo OCS-Host.exe was not found. Extract the whole ZIP first.
  pause
  exit /b 1
)
start "OCS LINK Host" /D "%~dp0host" "%~dp0host\OCS-Host.exe"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0OPEN-CONTROL.ps1"
pause
