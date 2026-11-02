# SECTION 11: Run on a DISCONNECTED, disposable, clean Windows x86 OR x64
# test machine/account. Unlike GitHub-hosted runners, this command explicitly
# refuses a network-connected host or an account containing AlTamam data.
# NOT a production restore test, human UAT signoff, or a final release.
# No network requests, package downloads, source tree or Python required.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x86', 'win-x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PortableZip,
    [Parameter(Mandatory = $true)][string]$SetupExe,
    [Parameter(Mandatory = $true)][string]$Sha256Sums,
    [switch]$IntegrityOnly,
    [switch]$ConfirmDisposableWindowsAccount,
    [string]$EvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition([bool]$value, [string]$description) {
    if (-not $value) { throw ('SECTION11 OFFLINE CHECK FAIL: ' + $description) }
}
function Hash-File([string]$file) {
    return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Invoke-Checked([string]$file, [string[]]$arguments, [int]$seconds) {
    Assert-Condition (Test-Path -LiteralPath $file -PathType Leaf) ('Missing executable: ' + $file)
    $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru
    try {
        if (-not $process.WaitForExit($seconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw ('Process timed out: ' + (Split-Path -Leaf $file))
        }
        Assert-Condition ($process.ExitCode -eq 0) (
            'Nonzero exit ' + $process.ExitCode + ': ' + (Split-Path -Leaf $file))
    }
    finally { $process.Dispose() }
}
function Invoke-NativeSmoke([string]$file, [string]$label, [bool]$deep) {
    Invoke-Checked $file @('--layout-selfcheck') 90
    if ($deep) {
        $trace = Join-Path $work ('webview-' + $label + '.txt')
        $previous = [Environment]::GetEnvironmentVariable('ALTAMAM_WEBVIEW_SELFCHECK_TRACE')
        try {
            $env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE = $trace
            Invoke-Checked $file @('--webview-selfcheck', '--webview-ui-e2e',
                '--webview-bridge-e2e') 240
            Assert-Condition (Test-Path -LiteralPath $trace -PathType Leaf) (
                'Real WPF/WebView2 trace absent for ' + $label)
            $real = [IO.File]::ReadAllText($trace)
            foreach ($required in @(
                'UI-E2E PASS real Windows RTL/modal/form/validation/Escape/focus',
                'PASS actual HTML-C#-SQLCipher-HTML roundtrip and forbidden requests',
                'PASS real Windows GeneralAdmin Admin Supervisor SQLCipher bridge authorizations',
                'PASS all 69 manifest routes opened in real Windows WebView2'
            )) {
                Assert-Condition ($real.Contains($required)) (
                    'Real native Windows/SQLCipher evidence missing for ' + $label +
                    ': ' + $required)
            }
        }
        finally {
            if ($null -eq $previous) {
                Remove-Item Env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE -ErrorAction SilentlyContinue
            }
            else { $env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE = $previous }
        }
    }
    else { Invoke-Checked $file @('--webview-selfcheck') 120 }
}
function Require-NoProductionData([string]$root, [string]$sentinelPath,
                                   [string]$expectedSentinel) {
    foreach ($file in @('altamam.db', 'db.key.protected',
                       'altamam.db-wal', 'altamam.db-shm')) {
        Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $root $file))) (
            'Real user data unexpectedly appeared: ' + $file)
    }
    Assert-Condition (Test-Path -LiteralPath $sentinelPath -PathType Leaf) (
        'The synthetic AppData preservation marker was deleted')
    Assert-Condition ((Hash-File $sentinelPath) -eq $expectedSentinel) (
        'The synthetic AppData marker changed')
}
function Write-Evidence([string]$status, [string]$detail) {
    if ([string]::IsNullOrWhiteSpace($EvidencePath)) { return }
    $record = [ordered]@{
        schema = 'AlTamam.Section11.IndependentOfflineSmoke.v1'
        status = $status
        scope = 'Automated disposable-device smoke ONLY; NOT human UAT, cross-PC recovery or restore acceptance'
        runUtc = [DateTimeOffset]::UtcNow.ToString('O')
        windowsBuild = [Environment]::OSVersion.VersionString
        is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
        arch = $Architecture
        version = $Version
        portable = (Split-Path -Leaf $PortableZip)
        portableSha256 = $portableHash
        setup = (Split-Path -Leaf $SetupExe)
        setupSha256 = $setupHash
        network = 'All Windows network adapters reported not Up before execution'
        resultDetail = $detail
        operatorRoleChecks = 'Synthetic WPF/WebView2 role bridge probes only'
        actualFirstRunAttendanceExcelAndRestore = 'NOT_EXECUTED'
        physicalPowerFailureAndCrossMachineRecovery = 'NOT_EXECUTED'
        independentVerifierAndOwnerSignoff = 'PENDING'
    }
    $target = [IO.Path]::GetFullPath($EvidencePath)
    $dir = [IO.Path]::GetDirectoryName($target)
    Assert-Condition ([IO.Directory]::Exists($dir)) 'Evidence output directory must already exist'
    Assert-Condition (-not [IO.File]::Exists($target)) 'Never overwrite prior evidence'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($target, ($record | ConvertTo-Json -Depth 5), $utf8)
}

# Every package and manifest check runs BEFORE any disk or installer change.
Assert-Condition ($Version -cmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,80}$') 'Unsafe version'
$expectedZip = 'AlTamamDaily_' + $Version + '_Portable_' + $Architecture + '.zip'
$expectedSetup = 'AlTamamDaily_' + $Version + '_Setup_' + $Architecture + '.exe'
Assert-Condition ((Split-Path -Leaf $PortableZip) -ceq $expectedZip) 'Incorrect Portable filename'
Assert-Condition ((Split-Path -Leaf $SetupExe) -ceq $expectedSetup) 'Incorrect Setup filename'
foreach ($file in @($PortableZip, $SetupExe, $Sha256Sums)) {
    Assert-Condition (Test-Path -LiteralPath $file -PathType Leaf) (
        'Required file is missing: ' + $file)
}
$lines = @(Get-Content -LiteralPath $Sha256Sums -Encoding UTF8)
Assert-Condition ($lines.Count -eq 2) 'SHA256SUMS must contain exactly two records'
$expectedHashes = @{}
foreach ($line in $lines) {
    Assert-Condition ($line -cmatch '^([0-9a-f]{64})  ([A-Za-z0-9._+-]+)$') (
        'Malformed SHA256SUMS record')
    $digest = $Matches[1]
    $name = $Matches[2]
    Assert-Condition (-not $expectedHashes.ContainsKey($name)) 'Duplicated checksum record'
    $expectedHashes[$name] = $digest
}
Assert-Condition ($expectedHashes.Count -eq 2) 'Checksum inventory not exactly two'
Assert-Condition ($expectedHashes.ContainsKey($expectedZip)) 'Portable missing in SHA256SUMS'
Assert-Condition ($expectedHashes.ContainsKey($expectedSetup)) 'Setup missing in SHA256SUMS'
$portableHash = Hash-File $PortableZip
$setupHash = Hash-File $SetupExe
Assert-Condition ($portableHash -ceq $expectedHashes[$expectedZip]) 'Portable SHA256 mismatch'
Assert-Condition ($setupHash -ceq $expectedHashes[$expectedSetup]) 'Setup SHA256 mismatch'
Assert-Condition ((Get-Item -LiteralPath $PortableZip).Length -gt 0) 'Empty Portable'
Assert-Condition ((Get-Item -LiteralPath $SetupExe).Length -gt 0) 'Empty Setup'

if ($IntegrityOnly) {
    Write-Host 'FILE INTEGRITY ONLY PASS: two exact customer names and actual file SHA-256 agree; NO Windows application or offline UAT was executed'
    return
}

Assert-Condition $ConfirmDisposableWindowsAccount.IsPresent (
    'Full smoke requires -ConfirmDisposableWindowsAccount on an isolated test PC')
Assert-Condition (-not [string]::IsNullOrWhiteSpace($EvidencePath)) (
    'An explicit new evidence JSON path is required')
Assert-Condition ($Architecture -ceq $(if ([Environment]::Is64BitOperatingSystem) {
    'win-x64'
} else { 'win-x86' })) 'Must run each package on its matching actual Windows architecture'
Assert-Condition ($null -ne (Get-Command Get-NetAdapter -ErrorAction SilentlyContinue)) (
    'Cannot verify offline state: Get-NetAdapter is unavailable')
$connected = @(Get-NetAdapter -ErrorAction Stop |
    Where-Object { $_.Status -eq 'Up' })
Assert-Condition ($connected.Count -eq 0) (
    'Disable ALL network adapters before testing: not an offline environment')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = New-Object Security.Principal.WindowsPrincipal($identity)
Assert-Condition ($admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) (
    'The isolated test account must run elevated to install genuine Inno Setup')
# Prevent a test installer with a matching AppId from modifying any existing
# application registration, even if the current account is otherwise clean.
$uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($root in $uninstallRoots) {
    if (Test-Path -LiteralPath $root) {
        $matches = @(Get-ChildItem -LiteralPath $root -ErrorAction Stop |
            Where-Object { $_.PSChildName -match '(?i)B94D3F15|3A156EF1' })
        Assert-Condition ($matches.Count -eq 0) (
            'An existing AlTamam Installer registration is present: use a clean VM')
    }
}
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AlTamam'
if (Test-Path -LiteralPath $dataRoot) {
    $existing = @(Get-ChildItem -LiteralPath $dataRoot -Force -Recurse -ErrorAction Stop |
        Select-Object -First 1)
    Assert-Condition ($existing.Count -eq 0) (
        'AlTamam AppData is not empty. Never test on an existing account.')
}
$work = Join-Path $env:TEMP ('altamam-independent-offline-' + [guid]::NewGuid().ToString('N'))
$portable = Join-Path $work 'portable'
$relocated = Join-Path $work 'portable-relocated'
$installed = Join-Path $work 'installed'
$backups = Join-Path $dataRoot 'backups'
$sentinel = Join-Path $backups ('independent-smoke-' + [guid]::NewGuid().ToString('N') + '.txt')
$sentinelHash = ''
$uninstaller = Join-Path $installed 'unins000.exe'
$success = $false
try {
    New-Item -ItemType Directory -Path $portable, $backups -Force | Out-Null
    [IO.File]::WriteAllText($sentinel, 'Disposable-only marker: no live personnel', [Text.Encoding]::UTF8)
    $sentinelHash = Hash-File $sentinel
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $portable
    $portableExe = Join-Path $portable 'AlTamam.DesktopHost.exe'
    Assert-Condition (Test-Path -LiteralPath $portableExe -PathType Leaf) 'Portable ZIP lacks WPF EXE'
    Invoke-NativeSmoke $portableExe 'portable' $true
    Require-NoProductionData $dataRoot $sentinel $sentinelHash

    Move-Item -LiteralPath $portable -Destination $relocated
    $relocatedExe = Join-Path $relocated 'AlTamam.DesktopHost.exe'
    Invoke-NativeSmoke $relocatedExe 'moved-portable' $false
    Require-NoProductionData $dataRoot $sentinel $sentinelHash

    $installArgs = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', ('/DIR="' + $installed + '"'))
    Invoke-Checked $SetupExe $installArgs 300
    $installedExe = Join-Path $installed 'AlTamam.DesktopHost.exe'
    Assert-Condition (Test-Path -LiteralPath $uninstaller -PathType Leaf) 'Real uninstaller missing'
    Assert-Condition ((Hash-File $installedExe) -ceq (Hash-File $relocatedExe)) (
        'Portable and installed executable bytes differ')
    Invoke-NativeSmoke $installedExe 'installed' $true
    Require-NoProductionData $dataRoot $sentinel $sentinelHash
    Invoke-Checked $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 240
    Assert-Condition (-not (Test-Path -LiteralPath $installedExe)) (
        'Uninstall did not remove installed WPF executable')
    Require-NoProductionData $dataRoot $sentinel $sentinelHash

    Invoke-Checked $SetupExe $installArgs 300
    $installedExe = Join-Path $installed 'AlTamam.DesktopHost.exe'
    Invoke-NativeSmoke $installedExe 'reinstalled' $false
    Require-NoProductionData $dataRoot $sentinel $sentinelHash
    $success = $true
    Write-Evidence 'AUTOMATED_OFFLINE_SMOKE_PASS_NOT_FINAL_UAT' (
        'Real offline Portable, relocated Portable, installed/reinstalled WPF, ' +
        'SQLCipher native synthetic bridge, uninstall and unchanged test AppData')
    Write-Host 'INDEPENDENT OFFLINE SMOKE PASSED: real binaries on clean disconnected Windows; actual human UAT, older-to-newer upgrade, power failure and restore remain PENDING'
}
catch {
    $failure = $_.Exception.Message
    try { Write-Evidence 'AUTOMATED_OFFLINE_SMOKE_FAILED' $failure }
    catch { Write-Warning ('Failure evidence could not be written: ' + $_.Exception.Message) }
    throw
}
finally {
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            Invoke-Checked $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 240
        }
        catch { Write-Warning 'Disposable installer cleanup was not fully completed; inspect the clean test VM.' }
    }
    if (Test-Path -LiteralPath $sentinel -PathType Leaf) {
        Remove-Item -LiteralPath $sentinel -Force
    }
    if (Test-Path -LiteralPath $work) {
        try { Remove-Item -LiteralPath $work -Recurse -Force }
        catch { Write-Warning 'Disposable working files could not all be removed.' }
    }
}
