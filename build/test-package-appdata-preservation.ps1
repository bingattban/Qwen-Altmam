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

# Cold WebView2 startup on win-x86 Windows runners is sometimes slow.  Keep
# the SAME real navigation/title/exit-code gate as release pre-package checks,
# with at most two independent attempts (the host creates a fresh profile).
# Never accept a nonzero exit or timeout as success; surface the native phases.
function Run-PackagedWebViewChecked([string]$file, [string]$phase) {
    Assert-True (Test-Path -LiteralPath $file -PathType Leaf) "Executable absent: $file"
    $originalTrace = [Environment]::GetEnvironmentVariable("ALTAMAM_WEBVIEW_SELFCHECK_TRACE")
    $passed = $false
    try {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $trace = Join-Path $work ("webview-" + ($phase -replace '[^A-Za-z0-9]', '-') +
                "-attempt-" + $attempt + ".log")
            Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
            $env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE = $trace
            Write-Host "PACKAGED WEBVIEW: $phase ($ExpectedArch), attempt $attempt of 2"
            try {
                Run-Checked $file @("--webview-selfcheck") 90
                $passed = $true
            }
            catch {
                Write-Warning "PACKAGED WEBVIEW attempt $attempt did not pass: $($_.Exception.Message)"
            }
            finally {
                if (Test-Path -LiteralPath $trace -PathType Leaf) {
                    Get-Content -LiteralPath $trace | ForEach-Object {
                        Write-Host "PACKAGED WEBVIEW TRACE: $_"
                    }
                }
                else {
                    Write-Host "PACKAGED WEBVIEW TRACE: no native trace was produced"
                }
            }
            if ($passed) { break }
        }
    }
    finally {
        if ($null -eq $originalTrace) {
            Remove-Item Env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE -ErrorAction SilentlyContinue
        }
        else { $env:ALTAMAM_WEBVIEW_SELFCHECK_TRACE = $originalTrace }
    }
    Assert-True $passed "WebView2 navigation failed for $phase ($ExpectedArch) after 2 bounded attempts"
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
$movedPortableDir = Join-Path $work "relocated-portable"
$portableExe = Join-Path $portableDir "AlTamam.DesktopHost.exe"
$movedPortableExe = Join-Path $movedPortableDir "AlTamam.DesktopHost.exe"
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
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $movedPortableDir "data"))) "$phase wrote adjacent relocated Portable data"
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
    Run-PackagedWebViewChecked $portableExe "Portable ZIP"
    Assert-DataPreserved "Portable direct run"
    $portableHash = Hash-OrAbsent $portableExe

    # Move the ACTUAL extracted Portable tree, not just the EXE. A customer can
    # relocate an existing Portable installation without moving the per-user
    # SQLCipher database, DPAPI-wrapped key or internal rescue backups.
    Move-Item -LiteralPath $portableDir -Destination $movedPortableDir
    Assert-True (Test-Path -LiteralPath $movedPortableExe -PathType Leaf) "Relocated Portable EXE missing"
    Assert-True ((Hash-OrAbsent $movedPortableExe) -eq $portableHash) "Relocated Portable executable changed"
    Run-Checked $movedPortableExe @("--layout-selfcheck") 90
    Run-PackagedWebViewChecked $movedPortableExe "Portable moved"
    Assert-DataPreserved "Portable move to new folder"

    # Fresh silent install from the REAL installer, not the raw publish directory.
    Run-Checked $installerExe (Inno-Args $installedDir)
    Assert-True (Test-Path -LiteralPath $installedExe -PathType Leaf) "Installer did not create runnable EXE"
    Assert-True (Test-Path -LiteralPath $uninstaller -PathType Leaf) "Installer did not create uninstaller"
    Assert-True ((Hash-OrAbsent $installedExe) -eq $portableHash) "Portable and Installer EXEs differ"
    Run-Checked $installedExe @("--layout-selfcheck") 90
    Run-PackagedWebViewChecked $installedExe "Installer"
    Assert-DataPreserved "Installer run"

    # Reinstall the SAME version in-place while installed: this is an automated
    # overwrite-preservation test, NOT evidence of an older-to-newer upgrade.
    Run-Checked $installerExe (Inno-Args $installedDir)
    Assert-True ((Hash-OrAbsent $installedExe) -eq $portableHash) "Same-version reinstall EXE differs"
    Assert-DataPreserved "Same-version in-place reinstall"

    # Verify unattended uninstall removes installed binaries but retains the
    # exact old encrypted database, key and rescue/backups location.
    Run-Checked $uninstaller @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") 180
    Assert-True (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) "Uninstall left program EXE behind"
    Assert-DataPreserved "Uninstall"

    Run-Checked $installerExe (Inno-Args $installedDir)
    Assert-True ((Hash-OrAbsent $installedExe) -eq $portableHash) "Reinstall EXE differs"
    Run-Checked $installedExe @("--layout-selfcheck") 90
    Run-PackagedWebViewChecked $installedExe "Reinstall"
    Assert-DataPreserved "Reinstall"
    Write-Host ("PACKAGE-PRESERVATION PASS: " + $ExpectedArch +
        "; genuine Portable + moved Portable + Installer + same-version in-place install/uninstall/reinstall; unchanged LocalAppData data/key")
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
