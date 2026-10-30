# Automated Windows PACKAGE acceptance subset, NOT end-to-end GUI/manual approval.
# Exercised on each architecture's fresh GitHub Windows runner after full tests:
# exact shipped ZIP, exact shipped Inno Setup EXE, install/uninstall/reinstall,
# real Windows WebView2 navigation for BOTH packaged EXEs, not only raw publish,
# same per-user AppData with no data relocation and no user-data deletion.
# Only the unique CI sentinel is removed. Existing encrypted records are untouched.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PortableArchive,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x86", "win-x64")][string]$ExpectedArch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-True([bool]$condition, [string]$description) {
    if (-not $condition) { throw "PACKAGE-ACCEPTANCE FAIL: $description" }
}

function Hash-OrAbsent([string]$path) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    return "<ABSENT>"
}

function Run-Checked([string]$file, [string[]]$arguments, [int]$timeoutSeconds = 210) {
    Assert-True (Test-Path -LiteralPath $file -PathType Leaf) "Executable absent: $file"
    $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru
    try {
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Timed out after $timeoutSeconds seconds: $file"
        }
        Assert-True ($process.ExitCode -eq 0) "Exit code $($process.ExitCode) from $file"
    }
    finally {
        $process.Dispose()
    }
}

function Inno-Args([string]$dir) {
    # Quote /DIR explicitly for spaces in ephemeral Windows runner paths.
    return @("/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", ('/DIR="' + $dir + '"'))
}

$portableZip = (Resolve-Path -LiteralPath $PortableArchive).Path
$installerExe = (Resolve-Path -LiteralPath $InstallerPath).Path
Assert-True ($portableZip.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) "Not a ZIP"
Assert-True ($installerExe.EndsWith(".exe", [StringComparison]::OrdinalIgnoreCase)) "Not an installer"
Assert-True (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) "Requires isolated Windows CI runner"
$work = Join-Path $env:RUNNER_TEMP ("altamam-package-acceptance-" + [guid]::NewGuid().ToString("N"))
$portableDir = Join-Path $work "extracted-portable"
$installedDir = Join-Path $work "installed"
$portableExe = Join-Path $portableDir "AlTamam.DesktopHost.exe"
$installedExe = Join-Path $installedDir "AlTamam.DesktopHost.exe"
$uninstaller = Join-Path $installedDir "unins000.exe"

$local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
Assert-True (-not [string]::IsNullOrWhiteSpace($local)) "Windows LocalAppData unavailable"
$dataRoot = Join-Path $local "AlTamam"
$backups = Join-Path $dataRoot "backups"
$sentinel = Join-Path $backups ("ci-preservation-" + [guid]::NewGuid().ToString("N") + ".sentinel")
$baseline = @{}
$watch = @("altamam.db", "db.key.protected", "altamam.db-wal", "altamam.db-shm")
foreach ($name in $watch) {
    $baseline[$name] = Hash-OrAbsent (Join-Path $dataRoot $name)
}

function Assert-DataPreserved([string]$phase) {
    foreach ($name in $watch) {
        $actual = Hash-OrAbsent (Join-Path $dataRoot $name)
        Assert-True ($actual -eq $baseline[$name]) "$phase changed existing $name or created one"
    }
    Assert-True (Test-Path -LiteralPath $sentinel -PathType Leaf) "$phase deleted AppData sentinel"
    Assert-True ((Hash-OrAbsent $sentinel) -eq $script:sentinelHash) "$phase altered AppData sentinel"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $portableDir "data"))) "$phase wrote adjacent Portable data"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installedDir "data"))) "$phase wrote adjacent Installer data"
}

try {
    New-Item -ItemType Directory -Path $portableDir, $backups -Force | Out-Null
    [IO.File]::WriteAllText($sentinel, "Synthetic-only preservation marker " + [guid]::NewGuid().ToString("N"))
    $sentinelHash = Hash-OrAbsent $sentinel

    Expand-Archive -LiteralPath $portableZip -DestinationPath $portableDir -Force
    Assert-True (Test-Path -LiteralPath $portableExe -PathType Leaf) "Portable ZIP lacks an actual EXE"
    & python build/validate-layout.py $portableDir --portable --arch $ExpectedArch
    Assert-True ($LASTEXITCODE -eq 0) "Unzipped Portable failed real PE and offline dependency inspection"
    Run-Checked $portableExe @("--layout-selfcheck") 90
    # Real Windows WebView2 navigation from the extracted customer-facing ZIP.
    # This is an automated smoke test, NOT interactive end-to-end or disconnected physical UAT.
    Run-Checked $portableExe @("--webview-selfcheck") 90
    Assert-DataPreserved "Portable direct run"

    # Fresh silent install from the REAL installer, not the raw publish directory.
    Run-Checked $installerExe (Inno-Args $installedDir)
    Assert-True (Test-Path -LiteralPath $installedExe -PathType Leaf) "Installer did not create runnable EXE"
    Assert-True (Test-Path -LiteralPath $uninstaller -PathType Leaf) "Installer did not create uninstaller"
    $portableHash = Hash-OrAbsent $portableExe
    Assert-True ((Hash-OrAbsent $installedExe) -eq $portableHash) "Portable and Installer EXEs differ"
    Run-Checked $installedExe @("--layout-selfcheck") 90
    Run-Checked $installedExe @("--webview-selfcheck") 90
    Assert-DataPreserved "Installer run"

    # Verify unattended uninstall removes installed binaries but retains the
    # exact old encrypted database, key and rescue/backups location.
    Run-Checked $uninstaller @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") 180
    Assert-True (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) "Uninstall left program EXE behind"
    Assert-DataPreserved "Uninstall"

    Run-Checked $installerExe (Inno-Args $installedDir)
    Assert-True ((Hash-OrAbsent $installedExe) -eq $portableHash) "Reinstall EXE differs"
    Run-Checked $installedExe @("--layout-selfcheck") 90
    Run-Checked $installedExe @("--webview-selfcheck") 90
    Assert-DataPreserved "Reinstall"
    Write-Host ("PACKAGE-PRESERVATION PASS: " + $ExpectedArch +
        "; genuine Portable + Installer + uninstall/reinstall; unchanged LocalAppData data/key")
}
finally {
    # Never clean/rename the application database or DPAPI key. Only delete
    # the sentinel with our random name and our isolated extracted files.
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try { Run-Checked $uninstaller @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") 180 }
        catch { Write-Warning "Temporary test installer cleanup: $($_.Exception.Message)" }
    }
    if (Test-Path -LiteralPath $sentinel -PathType Leaf) {
        Remove-Item -LiteralPath $sentinel -Force
    }
    if (Test-Path -LiteralPath $work) {
        try { Remove-Item -LiteralPath $work -Recurse -Force }
        catch { Write-Warning "Ephemeral package folder retained: $($_.Exception.Message)" }
    }
}
