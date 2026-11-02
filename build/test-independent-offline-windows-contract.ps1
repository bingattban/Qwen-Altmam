# Negative and positive package-integrity checks on genuine Windows PowerShell.
# The fixture bytes are NOT executables. No GUI, installer, network or user DB
# is touched here; success is NEVER independent physical offline UAT.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'verify-clean-offline-candidate.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
    throw 'Independent offline acceptance validator script is missing'
}
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $script, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ('Invalid PowerShell syntax: ' + ($parseErrors -join '; ')) }
$source = Get-Content -LiteralPath $script -Raw
foreach ($mustRemain in @(
    'ConfirmDisposableWindowsAccount', 'Get-NetAdapter',
    'Get-FileHash', 'SourceSha', 'ReleaseRunId', 'BuildMetadata',
    'db.key.protected', 'altamam.db', 'unins000.exe',
    '--webview-ui-e2e', '--webview-bridge-e2e',
    'AUTOMATED_OFFLINE_SMOKE_PASS_NOT_FINAL_UAT',
    'actualFirstRunAttendanceExcelAndRestore',
    'independentVerifierAndOwnerSignoff'
)) {
    if (-not $source.Contains($mustRemain)) {
        throw ('Unsafe regression: offline acceptance guard missing: ' + $mustRemain)
    }
}

$work = Join-Path $env:TEMP ('altamam-offline-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $version = '0.0.884-ci'
    $zip = Join-Path $work "AlTamamDaily_${version}_Portable_win-x64.zip"
    $setup = Join-Path $work "AlTamamDaily_${version}_Setup_win-x64.exe"
    $sums = Join-Path $work 'SHA256SUMS.txt'
    $metadataPath = Join-Path $work 'BUILD_METADATA.json'
    [IO.File]::WriteAllBytes($zip, [Text.Encoding]::ASCII.GetBytes('SYNTHETIC TEST ZIP; NOT A REAL PROGRAM'))
    [IO.File]::WriteAllBytes($setup, [Text.Encoding]::ASCII.GetBytes('SYNTHETIC TEST INSTALLER; NOT A REAL PROGRAM'))
    $zipSum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $setupSum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($sums, "$zipSum  $(Split-Path -Leaf $zip)`n$setupSum  $(Split-Path -Leaf $setup)`n")
    $manifest = @{
        version = $version
        commit = ('a' * 40)
        sourceHeadSha = ('a' * 40)
        targetArchitecture = 'Windows-64bit'
        runtimeIdentifier = 'win-x64'
        architectureBitness = 64
        files = @(
            @{file = (Split-Path -Leaf $zip); sha256 = $zipSum; bytes = (Get-Item $zip).Length},
            @{file = (Split-Path -Leaf $setup); sha256 = $setupSum; bytes = (Get-Item $setup).Length}
        )
    }
    [IO.File]::WriteAllText($metadataPath, ($manifest | ConvertTo-Json -Depth 5))
    $cliArgs = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script,
        '-Version',$version,'-Architecture','win-x64','-PortableZip',$zip,
        '-SetupExe',$setup,'-Sha256Sums',$sums,
        '-BuildMetadata',$metadataPath,'-IntegrityOnly')

    & powershell.exe @cliArgs
    if ($LASTEXITCODE -ne 0) { throw 'Matching exact customer files and SHA-256 were rejected' }

    function Assert-Rejected([string[]]$candidate, [string]$reason) {
        $original = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & powershell.exe @candidate *> $null
            if ($LASTEXITCODE -eq 0) { throw ('Untrusted input accepted: ' + $reason) }
        }
        finally { $ErrorActionPreference = $original }
    }

    $badVersions = @('../escape', 'version with spaces', '.hidden')
    foreach ($bad in $badVersions) {
        $case = @($cliArgs)
        $case[7] = $bad
        Assert-Rejected $case ("unsafe version " + $bad)
    }
    $wrongArch = @($cliArgs)
    $wrongArch[9] = 'win-x86'
    Assert-Rejected $wrongArch 'wrong architecture'
    Add-Content -LiteralPath $zip -Value 'TAMPERED'
    Assert-Rejected $cliArgs 'tampered portable'
    [IO.File]::WriteAllBytes($zip, [Text.Encoding]::ASCII.GetBytes('SYNTHETIC TEST ZIP; NOT A REAL PROGRAM'))
    [IO.File]::AppendAllText($sums, "$zipSum  $(Split-Path -Leaf $zip)`n")
    Assert-Rejected $cliArgs 'duplicate checksum'
    [IO.File]::WriteAllText($sums, "$zipSum  $(Split-Path -Leaf $zip)`n$setupSum  $(Split-Path -Leaf $setup)`n")
    $manifest.sourceHeadSha = ('f' * 40)
    [IO.File]::WriteAllText($metadataPath, ($manifest | ConvertTo-Json -Depth 5))
    Assert-Rejected $cliArgs 'stale or different source HEAD metadata'
    Write-Host 'SECTION11 OFFLINE VALIDATOR CONTRACT PASS: exact-name and SHA integrity, unsafe version, arch mismatch, tamper and duplicate rejected; physical UAT NOT EXECUTED'
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
