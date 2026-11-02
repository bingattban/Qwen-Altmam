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
$work = Join-Path $env:TEMP ('altamam-offline-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $version = '0.0.884-ci'
    $zip = Join-Path $work "AlTamamDaily_${version}_Portable_win-x64.zip"
    $setup = Join-Path $work "AlTamamDaily_${version}_Setup_win-x64.exe"
    $sums = Join-Path $work 'SHA256SUMS.txt'
    [IO.File]::WriteAllBytes($zip, [Text.Encoding]::ASCII.GetBytes('SYNTHETIC TEST ZIP; NOT A REAL PROGRAM'))
    [IO.File]::WriteAllBytes($setup, [Text.Encoding]::ASCII.GetBytes('SYNTHETIC TEST INSTALLER; NOT A REAL PROGRAM'))
    $zipSum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $setupSum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($sums, "$zipSum  $(Split-Path -Leaf $zip)`n$setupSum  $(Split-Path -Leaf $setup)`n")
    $args = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script,
        '-Version',$version,'-Architecture','win-x64','-PortableZip',$zip,
        '-SetupExe',$setup,'-Sha256Sums',$sums,'-IntegrityOnly')
    & powershell.exe @args
    if ($LASTEXITCODE -ne 0) { throw 'Matching exact customer files and SHA-256 were rejected' }

    $badVersions = @('../escape', 'version with spaces', '.hidden')
    foreach ($bad in $badVersions) {
        $case = @($args)
        $case[8] = $bad
        & powershell.exe @case 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { throw "Unsafe version passed: $bad" }
    }
    $wrongArch = @($args)
    $wrongArch[10] = 'win-x86'
    & powershell.exe @wrongArch 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Wrong customer architecture passed' }
    Add-Content -LiteralPath $zip -Value 'TAMPERED'
    & powershell.exe @args 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Tampered portable passed SHA-256 check' }
    [IO.File]::WriteAllBytes($zip, [Text.Encoding]::ASCII.GetBytes('SYNTHETIC TEST ZIP; NOT A REAL PROGRAM'))
    [IO.File]::AppendAllText($sums, "$zipSum  $(Split-Path -Leaf $zip)`n")
    & powershell.exe @args 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Duplicate customer checksum passed' }
    Write-Host 'SECTION11 OFFLINE VALIDATOR CONTRACT PASS: exact-name and SHA integrity, unsafe version, arch mismatch, tamper and duplicate rejected; physical UAT NOT EXECUTED'
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
