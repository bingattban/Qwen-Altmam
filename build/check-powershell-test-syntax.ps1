# Fail closed before executing lengthy Windows tests if any required Phase 13
# PowerShell E2E/evidence script contains an unterminated string or other syntax error.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$paths = @(
    "build/test-webview-ui-e2e.ps1",
    "tests/AlTamam.WindowsE2E/Validate-Phase13Evidence.ps1",
    "tooling/perf/Collect-Phase13WindowsFacts.ps1"
)
foreach ($relative in $paths) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Missing mandatory Windows QA script: $relative"
    }
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $file, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($issue in $parseErrors) {
            Write-Error ("PowerShell syntax: " + $relative + " at " +
                $issue.Extent.StartLineNumber + ": " + $issue.Message) -ErrorAction Continue
        }
        throw "Windows QA parser rejected: $relative"
    }
}
Write-Host ("PowerShell QA syntax valid for " + $paths.Count + " mandatory scripts")
