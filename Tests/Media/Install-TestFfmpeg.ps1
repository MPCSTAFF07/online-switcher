$ErrorActionPreference = 'Stop'
if (Get-Command ffmpeg -ErrorAction SilentlyContinue) { exit 0 }
$archive = Join-Path $env:RUNNER_TEMP 'ffmpeg-test.zip'
$folder = Join-Path $env:RUNNER_TEMP 'ffmpeg-test'
Invoke-WebRequest -UseBasicParsing -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive
Expand-Archive $archive -DestinationPath $folder -Force
$exe = Get-ChildItem $folder -Filter ffmpeg.exe -Recurse | Select-Object -First 1
if (-not $exe) { throw 'FFmpeg test dependency download did not contain ffmpeg.exe' }
$exe.DirectoryName | Out-File $env:GITHUB_PATH -Append -Encoding utf8
