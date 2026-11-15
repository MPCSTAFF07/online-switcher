$ErrorActionPreference = 'Stop'
$ready = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    try { $null = Invoke-WebRequest -UseBasicParsing 'http://localhost:17640/' -TimeoutSec 1; $ready = $true; break }
    catch { Start-Sleep -Milliseconds 250 }
}
if (-not $ready) { Write-Host 'Host did not start. Check the OCS Host window.'; exit 1 }
$config = Get-Content (Join-Path $PSScriptRoot 'host\config.json') -Raw | ConvertFrom-Json
Write-Host ''
Write-Host 'OCS LINK control: http://localhost:17640'
Write-Host ('Admin password: ' + $config.AdminPassword)
Write-Host 'Keep the Host window open while receiving.'
Start-Process 'http://localhost:17640'
