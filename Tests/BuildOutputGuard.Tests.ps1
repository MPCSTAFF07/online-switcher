param([string]$PublishedOutput = (Join-Path $PSScriptRoot '..\dist\OCS-Link'))

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\BuildOutputGuard.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ocs-build-guard-' + [guid]::NewGuid().ToString('N'))
$child = $null
$locked = $null
$previousUrls = $env:OCS_URLS

function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    Write-Host "PASS $Name"
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $otherOutput = Join-Path $testRoot 'other'
    Assert-BuildOutputAvailable -OutputDirectory $otherOutput
    Check $true 'new output directory is accepted'

    $liveOutput = Join-Path $testRoot 'live'
    New-Item -ItemType Directory -Path $liveOutput -Force | Out-Null
    Copy-Item (Join-Path $PublishedOutput 'host') $liveOutput -Recurse
    $exe = Join-Path $liveOutput 'host\OCS-Host.exe'
    $env:OCS_URLS = 'http://127.0.0.1:0'
    $child = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $testRoot 'host.stdout.log') -RedirectStandardError (Join-Path $testRoot 'host.stderr.log')
    Start-Sleep -Milliseconds 800
    $child.Refresh()
    Check (-not $child.HasExited) 'isolated test host is running'

    $blocked = $false
    try { Assert-BuildOutputAvailable -OutputDirectory $liveOutput }
    catch { $blocked = $_.Exception.Message.Contains("PID $($child.Id)") }
    Check $blocked 'running target host is rejected with its PID before publish'

    Assert-BuildOutputAvailable -OutputDirectory $otherOutput
    $child.Refresh()
    Check (-not $child.HasExited) 'guard leaves running applications untouched and ignores other install paths'

    Stop-Process -Id $child.Id -ErrorAction SilentlyContinue
    $child.WaitForExit(5000) | Out-Null
    $child = $null
    Assert-BuildOutputAvailable -OutputDirectory $liveOutput
    Check $true 'closed host output becomes available for updating'

    $dll = Join-Path $otherOutput 'host\OCS-Host.dll'
    New-Item -ItemType Directory -Path (Split-Path $dll) -Force | Out-Null
    [IO.File]::WriteAllText($dll, 'lock test fixture')
    $locked = [IO.File]::Open($dll, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $blocked = $false
    try { Assert-BuildOutputAvailable -OutputDirectory $otherOutput }
    catch { $blocked = $_.Exception.Message.Contains('OCS-Host.dll') -and $_.Exception.Message.StartsWith('Build output cannot be updated:') }
    Check $blocked 'exclusive DLL lock is detected even without a named OCS process'
    $locked.Dispose(); $locked = $null
    Assert-BuildOutputAvailable -OutputDirectory $otherOutput
    Check ([IO.File]::ReadAllText($dll) -eq 'lock test fixture') 'lock check preserves existing file contents'
}
finally {
    if ($locked) { $locked.Dispose() }
    if ($child) { Stop-Process -Id $child.Id -ErrorAction SilentlyContinue }
    if ($null -eq $previousUrls) { Remove-Item Env:OCS_URLS -ErrorAction SilentlyContinue } else { $env:OCS_URLS = $previousUrls }
    Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
