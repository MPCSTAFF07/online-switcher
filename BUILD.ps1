param([switch]$InstallSdk)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$output = Join-Path $PSScriptRoot 'dist\OCS-Link'

function Find-Sdk8 {
    $candidates = @()
    foreach ($root in @($env:ProgramW6432, $env:ProgramFiles)) {
        if ($root) { $candidates += Join-Path $root 'dotnet\dotnet.exe' }
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path $candidate)) { continue }
        try {
            $sdks = @(& $candidate --list-sdks 2>$null)
            if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^8\.' })) { return $candidate }
        }
        catch { }
    }
    return $null
}

$dotnet = Find-Sdk8
if (-not $dotnet) {
    Write-Host '.NET 8 SDK is required to build. The runtime alone cannot build this project.'
    if (-not $InstallSdk) {
        throw 'Install the SDK first: winget install --id Microsoft.DotNet.SDK.8 --exact'
    }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'WinGet was not found. Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 and run BUILD.bat again.'
    }
    Write-Host 'Installing .NET 8 SDK. Approve the Windows installer if prompted.'
    & winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --architecture x64 --accept-source-agreements --accept-package-agreements
    $installExit = $LASTEXITCODE
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machinePath;$userPath;$env:Path"
    $dotnet = Find-Sdk8
    if (-not $dotnet) {
        throw "SDK installation was not detected (WinGet exit $installExit). Restart Windows Terminal, confirm dotnet --list-sdks shows 8.x, then run BUILD.bat again."
    }
}
$env:DOTNET_ROOT = Split-Path $dotnet
Write-Host "Using .NET SDK: $dotnet"

& $dotnet run --project Tests/Tests.csproj --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'RTP tests failed' }
& $dotnet publish Sender/Sender.csproj -c Release -r win-x64 --self-contained true -o "$output\sender"
if ($LASTEXITCODE -ne 0) { throw 'Sender build failed' }
& $dotnet publish Host/Host.csproj -c Release -r win-x64 --self-contained true -o "$output\host"
if ($LASTEXITCODE -ne 0) { throw 'Host build failed' }

foreach ($name in @('START-HOST.bat', 'START-SENDER.bat', 'SETUP-SENDER.bat', 'OPEN-CONTROL.ps1', 'README.md', 'VALIDATION.md', 'THIRD-PARTY.md', 'VERSION')) {
    Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $output $name) -Force
}
Copy-Item (Join-Path $PSScriptRoot 'deploy') $output -Recurse -Force
New-Item -ItemType Directory -Path "$output\sender\tools" -Force | Out-Null
Set-Content -Path "$output\sender\tools\README.txt" -Encoding UTF8 -Value 'Run SETUP-SENDER.bat to install FFmpeg, or place ffmpeg.exe in this folder. FFmpeg is not bundled.'

# Retain NuGet license files alongside the runtime's own licenses/notices.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageRoot = $env:NUGET_PACKAGES
if (-not $packageRoot) { $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages' }
$deps = Get-Content "$output\sender\OCS-Sender.deps.json" -Raw | ConvertFrom-Json
foreach ($library in $deps.libraries.PSObject.Properties.Name) {
    $id, $version = $library.Split('/', 2)
    $packageId = $id.ToLowerInvariant()
    $nupkg = Join-Path $packageRoot "$packageId\$version\$packageId.$version.nupkg"
    if (-not (Test-Path $nupkg)) { continue }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name -or $entry.FullName -notmatch '(?i)(license|notice|copying|\.nuspec$)') { continue }
            $licenseDir = Join-Path $output "licenses\nuget\$id-$version"
            New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $licenseDir $entry.Name), $true)
        }
    }
    finally { $archive.Dispose() }
}
Write-Host "Build completed: $output"
