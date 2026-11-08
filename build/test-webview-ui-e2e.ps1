# Real Windows WPF/WebView2 E2E on the compiled Release executable, without rebuilding.
# The --webview-selfcheck mode uses a temporary profile and never opens SQLCipher.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exe = Join-Path $repository "src/AlTamam.DesktopHost/bin/Release/net10.0-windows/AlTamam.DesktopHost.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "UI-E2E FAIL: built Windows executable absent: $exe"
}
# Two real native runs must succeed. The original combined 240-second process
# starved the stage-two recovery flow after running modal, bridge, and all 69 routes.
# Both runs retain independent random-key SQLCipher fixtures and isolated WebView2
# profiles; neither is a simulated outcome or a skipped security assertion.
$requiredPhases = @(
    @{
        Name = "modal-bridge-routes"
        Arguments = @("--webview-selfcheck","--webview-ui-e2e","--webview-bridge-e2e","--webview-split-recovery-e2e")
        Markers = @(
            "UI-E2E PASS real Windows RTL/modal/form/validation/Escape/focus",
            "PASS bundled offline WebUI title verified",
            "PASS actual HTML-C#-SQLCipher-HTML roundtrip and forbidden requests",
            "PASS all 69 manifest routes opened in real Windows WebView2",
            "PASS real Windows GeneralAdmin Admin Supervisor SQLCipher bridge authorizations",
            "PASS real Windows modal-confirmed internal transfer persisted exactly once in SQLCipher"
        )
    },
    @{
        Name = "recovery"
        Arguments = @("--webview-selfcheck","--webview-bridge-e2e","--webview-split-route-e2e")
        Markers = @(
            "PASS bundled offline WebUI title verified",
            "PASS actual HTML-C#-SQLCipher-HTML roundtrip and forbidden requests",
            "PASS R01 real backup restore-preview reset UI and independent encrypted rescue"
        )
    }
)
$previousTrace = [Environment]::GetEnvironmentVariable("ALTAMAM_WEBVIEW_SELFCHECK_TRACE")
$traces = [System.Collections.Generic.List[string]]::new()
$allEvidence = [System.Collections.Generic.List[string]]::new()
$process = $null
try {
    foreach ($phase in $requiredPhases) {
        $trace = Join-Path ([IO.Path]::GetTempPath()) ("AlTamam-UI-E2E-" + [Guid]::NewGuid().ToString("N") + ".log")
        $traces.Add($trace)
        $env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE = $trace
        Write-Host "UI-E2E: launching mandatory real Windows phase $($phase.Name)"
        $process = Start-Process -FilePath $exe -ArgumentList $phase.Arguments -PassThru
        if (-not $process.WaitForExit(240000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $diagnostic = if (Test-Path -LiteralPath $trace) {
                Get-Content -LiteralPath $trace -Raw
            } else { "<no native execution trace>" }
            throw "UI-E2E FAIL: $($phase.Name) real Windows process did not complete within 240 seconds; $diagnostic"
        }
        if ($process.ExitCode -ne 0) {
            $diagnostic = if (Test-Path -LiteralPath $trace) {
                Get-Content -LiteralPath $trace -Raw
            } else { "<no native execution trace>" }
            throw "UI-E2E FAIL: $($phase.Name) exit code $($process.ExitCode); $diagnostic"
        }
        if (-not (Test-Path -LiteralPath $trace -PathType Leaf)) {
            throw "UI-E2E FAIL: $($phase.Name) no real WebView2 execution trace"
        }
        $phaseEvidence = Get-Content -LiteralPath $trace -Raw
        foreach ($marker in $phase.Markers) {
            if (-not $phaseEvidence.Contains($marker)) {
                throw "UI-E2E FAIL: $($phase.Name) required real Windows evidence missing: $marker; $phaseEvidence"
            }
        }
        $allEvidence.Add("$($phase.Name): $phaseEvidence")
        Write-Host "UI-E2E PASS: $($phase.Name) verified in a genuine bounded Windows process"
        $process = $null
    }
    $evidence = [string]::Join("`n", $allEvidence)
    # An evidence file is written only AFTER the native process exits 0 and all
    # mandatory real-WebView2 trace markers are verified. Never predeclare success.
    $phase13 = [Environment]::GetEnvironmentVariable("ALTAMAM_PHASE13_EVIDENCE_PATH")
    if (-not [string]::IsNullOrWhiteSpace($phase13)) {
        $gitSha = & git -C $repository rev-parse HEAD
        $gitSucceeded = $?
        $checkoutSha = ([string]$gitSha).Trim()
        if (-not $gitSucceeded -or $checkoutSha -notmatch '^[a-fA-F0-9]{40}$') {
            throw "UI-E2E evidence: cannot identify tested checkout commit"
        }
        $sourceSha = [Environment]::GetEnvironmentVariable("ALTAMAM_SOURCE_HEAD_SHA")
        if ([string]::IsNullOrWhiteSpace($sourceSha)) { $sourceSha = $checkoutSha }
        $record = [ordered]@{
            schema = "AlTamam.Phase13.RealWindowsE2E.v1"
            sourceHeadSha = $sourceSha
            actualCheckoutSha = $checkoutSha
            githubRunId = [Environment]::GetEnvironmentVariable("GITHUB_RUN_ID")
            githubWorkflow = [Environment]::GetEnvironmentVariable("GITHUB_WORKFLOW")
            checkedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            operatingSystem = [Environment]::OSVersion.VersionString
            realWpfWebView2ProcessExitedZero = $true
            traceAttestedChecks = [ordered]@{
                arabicRtlModalFormValidationEscapeFocus = $true
                bundledOfflineUiTitle = $true
                sqlCipherBridgeRoundtripAndForbiddenRequests = $true
                all69CanonicalManifestRoutesOpened = $true
                generalAdminAdminSupervisorBridgeRoleProbes = $true
                modalConfirmedInternalTransferSqlCipher = $true
                backupPreviewResetAndEncryptedRescueSqlCipher = $true
            }
            notExecutedByThisProbe = @(
                "Every role exercising every nested screen business control",
                "1024x768 / 1366x768 / high-DPI screen reader visual acceptance",
                "10,000-row 31-day visible grid end-to-end latency",
                "Real physical power interruption and cross-machine restore"
            )
        }
        $directory = Split-Path -Parent $phase13
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $phase13 -Encoding utf8
    }
    Write-Host "UI-E2E PASS: real Windows WPF/WebView2 + synthetic SQLCipher bridge roundtrip and all three roles"
}
finally {
    [Environment]::SetEnvironmentVariable("ALTAMAM_WEBVIEW_SELFCHECK_TRACE", $previousTrace)
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true) } catch { }
    }
    foreach ($path in $traces) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
