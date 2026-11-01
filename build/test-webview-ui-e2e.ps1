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
$trace = Join-Path ([IO.Path]::GetTempPath()) ("AlTamam-UI-E2E-" + [Guid]::NewGuid().ToString("N") + ".log")
$previousTrace = [Environment]::GetEnvironmentVariable("ALTAMAM_WEBVIEW_SELFCHECK_TRACE")
$process = $null
try {
    $env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE = $trace
    $process = Start-Process -FilePath $exe -ArgumentList @("--webview-selfcheck","--webview-ui-e2e","--webview-bridge-e2e") -PassThru
    if (-not $process.WaitForExit(240000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "UI-E2E FAIL: Windows WebView2 did not complete within 240 seconds"
    }
    if ($process.ExitCode -ne 0) {
        $diagnostic = if (Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace -Raw } else { "<no trace>" }
        throw "UI-E2E FAIL: exit code $($process.ExitCode); $diagnostic"
    }
    if (-not (Test-Path -LiteralPath $trace -PathType Leaf)) {
        throw "UI-E2E FAIL: no real WebView2 execution trace"
    }
    $evidence = Get-Content -LiteralPath $trace -Raw
    if ($evidence -notmatch 'UI-E2E PASS real Windows RTL/modal/form/validation/Escape/focus' -or
        $evidence -notmatch 'PASS bundled offline WebUI title verified' -or
        $evidence -notmatch 'PASS actual HTML-C#-SQLCipher-HTML roundtrip and forbidden requests' -or
        $evidence -notmatch 'PASS all 69 manifest routes opened in real Windows WebView2' -or
        $evidence -notmatch 'PASS real Windows GeneralAdmin Admin Supervisor SQLCipher bridge authorizations') {
        throw "UI-E2E FAIL: modal interaction or offline navigation evidence missing; $evidence"
    }
    # An evidence file is written only AFTER the native process exits 0 and all
    # mandatory real-WebView2 trace markers are verified. Never predeclare success.
    $phase13 = [Environment]::GetEnvironmentVariable("ALTAMAM_PHASE13_EVIDENCE_PATH")
    if (-not [string]::IsNullOrWhiteSpace($phase13)) {
        $checkoutSha = (& git -C $repository rev-parse HEAD | Select-Object -First 1).Trim()
        if ($LASTEXITCODE -ne 0 -or $checkoutSha -notmatch '^[a-fA-F0-9]{40}$') {
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
    Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
}
