@echo off
setlocal
echo Installing FFmpeg for OCS LINK Sender using Windows Package Manager.
echo The sender and host already include the .NET runtime.
where winget >nul 2>nul
if errorlevel 1 (
  echo Windows Package Manager was not found.
  echo Install FFmpeg manually and place ffmpeg.exe in sender\tools\ffmpeg.exe
  echo Official information: https://ffmpeg.org/download.html
  pause
  exit /b 1
)
winget install --id Gyan.FFmpeg --exact --accept-source-agreements --accept-package-agreements
if errorlevel 1 (
  echo Installation may have failed or FFmpeg may already be installed.
  echo Restart the sender and check its log.
)
echo Start OCS LINK Sender with START-SENDER.bat
pause
