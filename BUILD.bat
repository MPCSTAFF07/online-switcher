@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD.ps1" -InstallSdk
if errorlevel 1 (
  echo Build failed. Review the error above.
  pause
  exit /b 1
)
echo Ready: dist\OCS-Link
start "" "%~dp0dist\OCS-Link"
pause
