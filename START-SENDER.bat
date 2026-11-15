@echo off
setlocal
cd /d "%~dp0"
if not exist "sender\OCS-Sender.exe" (
  echo OCS-Sender.exe was not found. Extract the whole ZIP first.
  pause
  exit /b 1
)
start "OCS LINK Sender" /D "%~dp0sender" "%~dp0sender\OCS-Sender.exe"
