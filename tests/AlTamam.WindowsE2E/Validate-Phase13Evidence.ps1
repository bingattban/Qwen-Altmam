# Fail if an artifact claims tests that did not truly execute on Windows.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "Windows E2E evidence cannot be validated on a non-Windows host" }
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$gitSha = & git -C $root rev-parse HEAD
$gitSucceeded = $?
$checkoutSha = ([string]$gitSha).Trim()
if (-not $gitSucceeded -or $checkoutSha -notmatch '^[a-fA-F0-9]{40}$') {
    throw "Phase 13 cannot identify tested checkout commit"
}
$native = Get-Content -LiteralPath (Join-Path $EvidenceDir "windows-e2e.json") -Raw | ConvertFrom-Json
$hardware = Get-Content -LiteralPath (Join-Path $EvidenceDir "runner-facts.json") -Raw | ConvertFrom-Json
if ($checkoutSha -notmatch '^[a-fA-F0-9]{40}$' -or
    $native.actualCheckoutSha -ne $checkoutSha -or
    $hardware.actualCheckoutSha -ne $checkoutSha -or
    $native.sourceHeadSha -ne $hardware.sourceHeadSha -or
    $native.realWpfWebView2ProcessExitedZero -ne $true) {
    throw "Phase 13 evidence SHA or native Windows E2E result mismatch"
}
foreach ($check in @(
    "arabicRtlModalFormValidationEscapeFocus",
    "bundledOfflineUiTitle",
    "sqlCipherBridgeRoundtripAndForbiddenRequests",
    "all69CanonicalManifestRoutesOpened",
    "generalAdminAdminSupervisorBridgeRoleProbes"
)) {
    if ($native.traceAttestedChecks.$check -ne $true) {
        throw "Phase 13 E2E evidence claim missing: $check"
    }
}
if ($native.notExecutedByThisProbe.Count -lt 4) {
    throw "Phase 13 omitted-coverage disclosure missing"
}
Write-Host "Phase 13 validated: native WPF process + three-role bridge and 69-route evidence tied to $checkoutSha"
