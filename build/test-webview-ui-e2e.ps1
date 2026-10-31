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
        $evidence -notmatch 'PASS all 69 manifest routes opened in real Windows WebView2') {
        throw "UI-E2E FAIL: modal interaction or offline navigation evidence missing; $evidence"
    }
    Write-Host "UI-E2E PASS: real Windows WPF/WebView2 + synthetic SQLCipher bridge roundtrip and forbidden requests"
}
finally {
    [Environment]::SetEnvironmentVariable("ALTAMAM_WEBVIEW_SELFCHECK_TRACE", $previousTrace)
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true) } catch { }
    }
    Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
}
