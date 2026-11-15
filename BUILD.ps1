$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$output = Join-Path $PSScriptRoot 'dist\OCS-Link'

dotnet run --project Tests/Tests.csproj --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'RTP tests failed' }
dotnet publish Sender/Sender.csproj -c Release -r win-x64 --self-contained true -o "$output\sender"
if ($LASTEXITCODE -ne 0) { throw 'Sender build failed' }
dotnet publish Host/Host.csproj -c Release -r win-x64 --self-contained true -o "$output\host"
if ($LASTEXITCODE -ne 0) { throw 'Host build failed' }

foreach ($name in @('START-HOST.bat', 'START-SENDER.bat', 'SETUP-SENDER.bat', 'OPEN-CONTROL.ps1', 'README.md', 'VALIDATION.md', 'THIRD-PARTY.md', 'VERSION')) {
    Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $output $name) -Force
}
Copy-Item (Join-Path $PSScriptRoot 'deploy') $output -Recurse -Force
New-Item -ItemType Directory -Path "$output\sender\tools" -Force | Out-Null
Set-Content -Path "$output\sender\tools\README.txt" -Encoding UTF8 -Value 'Run SETUP-SENDER.bat to install FFmpeg, or place ffmpeg.exe in this folder. FFmpeg is not bundled.'

# Retain NuGet license files alongside the runtime's own licenses/notices.
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
