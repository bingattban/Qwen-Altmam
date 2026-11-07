# Fail-closed process watchdog: shared by real x86/x64 packaged acceptance.
Set-StrictMode -Version Latest
function Get-PackagedBridgeTimeoutSeconds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("win-x86", "win-x64")][string]$Runtime
    )
    # Real successful x86 bridge runs measured 271.95s and 275.79s in
    # release 42387825788. A 300s cap left less than 9% runner headroom;
    # main release 42391468076 then hit that cap twice. Keep a finite
    # architecture-specific budget; no timeout ever counts as success.
    switch ($Runtime) {
        "win-x86" { return 480 }
        "win-x64" { return 300 }
        default { throw "Unsupported packaged bridge runtime: $Runtime" }
    }
}

function Invoke-CheckedPackageProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 210
    )
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "PACKAGE PROCESS MISSING: $FilePath"
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "PACKAGE PROCESS START: $FilePath; timeoutSeconds=$TimeoutSeconds"
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOutPid = $process.Id
            Write-Warning "PACKAGE PROCESS TIMEOUT: pid=$timedOutPid; budgetSeconds=$TimeoutSeconds"
            try { $process.Kill($true) }
            catch { Write-Warning "Process tree kill error: $($_.Exception.Message)" }
            # Never call the parameterless, potentially unbounded WaitForExit.
            try {
                if (-not $process.WaitForExit(10000)) {
                    Write-Warning "Timed-out process did not exit within bounded cleanup window: pid=$timedOutPid"
                }
            }
            catch { Write-Warning "Process cleanup wait error: $($_.Exception.Message)" }
            throw "PACKAGE PROCESS TIMEOUT: $FilePath; pid=$timedOutPid; budgetSeconds=$TimeoutSeconds"
        }
        if ($process.ExitCode -ne 0) {
            throw "PACKAGE PROCESS EXIT FAILURE: $FilePath; exit code $($process.ExitCode)"
        }
        Write-Host ("PACKAGE PROCESS PASS: $FilePath; elapsedSeconds=" + [Math]::Round($watch.Elapsed.TotalSeconds, 2))
    }
    finally {
        $watch.Stop()
        $process.Dispose()
    }
}
