# Native PowerShell Windows subprocess regression; synthetic processes only.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "package-process-watchdog.ps1")
$pwsh = Join-Path $PSHOME "pwsh.exe"
if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) {
    throw "A Windows PowerShell 7 process is required"
}
Invoke-CheckedPackageProcess -FilePath $pwsh -Arguments @("-NoProfile", "-NonInteractive", "-Command", "exit 0") -TimeoutSeconds 30
$rejectedExit = $false
try {
    Invoke-CheckedPackageProcess -FilePath $pwsh -Arguments @("-NoProfile", "-NonInteractive", "-Command", "exit 17") -TimeoutSeconds 30
}
catch {
    if ($_.Exception.Message -notmatch 'PACKAGE PROCESS EXIT FAILURE:.*exit code 17') { throw }
    $rejectedExit = $true
}
if (-not $rejectedExit) { throw "Nonzero packaged process exit was accepted" }
$clock = [Diagnostics.Stopwatch]::StartNew()
$rejectedTimeout = $false
try {
    Invoke-CheckedPackageProcess -FilePath $pwsh -Arguments @("-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30") -TimeoutSeconds 1
}
catch {
    if ($_.Exception.Message -notmatch 'PACKAGE PROCESS TIMEOUT:') { throw }
    $rejectedTimeout = $true
}
finally { $clock.Stop() }
if (-not $rejectedTimeout) { throw "Process timeout was accepted as successful" }
if ($clock.Elapsed.TotalSeconds -ge 18) { throw "Process timeout cleanup exceeded its bounded test window" }
$rejectedMissing = $false
try {
    Invoke-CheckedPackageProcess -FilePath (Join-Path $PSScriptRoot "nonexistent-package-executable.exe") -Arguments @("--layout-selfcheck") -TimeoutSeconds 5
}
catch {
    if ($_.Exception.Message -notmatch 'PACKAGE PROCESS MISSING:') { throw }
    $rejectedMissing = $true
}
if (-not $rejectedMissing) { throw "Missing package executable was accepted" }
Write-Host "PACKAGE WATCHDOG REGRESSION PASS: 0/17 exit, hard-timeout, missing-package negative paths"
