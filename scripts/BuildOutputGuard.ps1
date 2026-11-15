function Assert-BuildOutputAvailable {
    param([Parameter(Mandatory = $true)][string]$OutputDirectory)

    $executables = @(
        (Join-Path $OutputDirectory 'host\OCS-Host.exe'),
        (Join-Path $OutputDirectory 'sender\OCS-Sender.exe')
    ) | ForEach-Object { [IO.Path]::GetFullPath($_) }

    $blocking = @()
    foreach ($app in @(Get-Process -Name 'OCS-Host', 'OCS-Sender' -ErrorAction SilentlyContinue)) {
        $appPath = $null
        try { $appPath = $app.Path } catch { }
        if (-not $appPath) { continue }
        foreach ($expected in $executables) {
            if ([string]::Equals([IO.Path]::GetFullPath($appPath), $expected, [StringComparison]::OrdinalIgnoreCase)) {
                $blocking += "$($app.ProcessName) (PID $($app.Id))"
                break
            }
        }
    }
    if ($blocking.Count -gt 0) {
        throw "Close the running OCS application before updating: $($blocking -join ', '). Close its window or use the sender tray Exit menu, then run BUILD.bat again. Output: $OutputDirectory"
    }

    # Also catch locked DLLs when a process path is unavailable or the host was
    # started through dotnet rather than its apphost. Opening is non-destructive.
    foreach ($relative in @('host\OCS-Host.exe', 'host\OCS-Host.dll', 'sender\OCS-Sender.exe', 'sender\OCS-Sender.dll')) {
        $file = Join-Path $OutputDirectory $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $handle = $null
        try {
            $handle = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch {
            $problem = $_.Exception.GetBaseException()
            if ($problem -is [UnauthorizedAccessException]) {
                throw "Build output is not writable: $file. Check this folder's permissions before running BUILD.bat again."
            }
            if ($problem -is [IO.IOException]) {
                throw "Build output cannot be updated: $file. Close OCS Host and OCS Sender and retry BUILD.bat. Details: $($problem.Message)"
            }
            throw
        }
        finally { if ($handle) { $handle.Dispose() } }
    }
}
