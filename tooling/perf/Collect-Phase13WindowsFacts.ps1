# Phase 13 Windows-only hardware snapshot. Search timings are imported ONLY
# from an actual xUnit run that wrote ALTAMAM_PERF_REPORT, never guessed.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$MeasurementFile = ""
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "Real Windows evidence cannot be produced on a non-Windows host." }

$repository = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$checkoutSha = (& git -C $repository rev-parse HEAD | Select-Object -First 1).Trim()
if ($LASTEXITCODE -ne 0 -or $checkoutSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Phase 13: cannot identify checked-out commit"
}
$sourceSha = [Environment]::GetEnvironmentVariable("ALTAMAM_SOURCE_HEAD_SHA")
if ([string]::IsNullOrWhiteSpace($sourceSha)) { $sourceSha = $checkoutSha }

$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$system = Get-CimInstance Win32_ComputerSystem
$operatingSystem = Get-CimInstance Win32_OperatingSystem
if (-not $processor -or -not $system -or -not $operatingSystem) {
    throw "Phase 13: Windows hardware inventory unavailable"
}
$search = [ordered]@{ status = "NOT_EXECUTED"; sample = $null; source = $null }
if (-not [string]::IsNullOrWhiteSpace($MeasurementFile)) {
    if (-not (Test-Path -LiteralPath $MeasurementFile -PathType Leaf)) {
        throw "Phase 13: specified performance measurement absent"
    }
    $lines = @(Get-Content -LiteralPath $MeasurementFile |
        Where-Object { $_ -match '^SECTION01_(PERF|HW) ' })
    if (@($lines | Where-Object { $_ -match '^SECTION01_PERF personCount=10000 ' }).Count -ne 1) {
        throw "Phase 13: 10,000-person measurement was not executed or recorded"
    }
    $search.status = "MEASURED"
    $search.sample = @($lines)
    $search.source = $MeasurementFile
}
$report = [ordered]@{
    schema = "AlTamam.Phase13.WindowsRunnerMeasurements.v1"
    sourceHeadSha = $sourceSha
    actualCheckoutSha = $checkoutSha
    githubRunId = [Environment]::GetEnvironmentVariable("GITHUB_RUN_ID")
    capturedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    hardware = [ordered]@{
        cpuName = $processor.Name
        cores = [int]$processor.NumberOfCores
        logicalProcessors = [int]$processor.NumberOfLogicalProcessors
        physicalMemoryBytes = [long]$system.TotalPhysicalMemory
        osName = $operatingSystem.Caption
        osBuild = $operatingSystem.BuildNumber
        process64Bit = [Environment]::Is64BitProcess
    }
    tenThousandSyntheticSearch = $search
    otherPhase13Benchmarks = [ordered]@{
        tenThousandPeopleMultiYear31DayUI = "NOT_EXECUTED"
        sessionScopedExportLatencyAndThroughput = "NOT_EXECUTED"
        powerLossMovementAndBackup = "NOT_EXECUTED"
    }
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Phase 13 real Windows runner facts recorded for $checkoutSha"
