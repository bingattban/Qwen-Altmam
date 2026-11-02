# SECTION 11 — Independent clean, disconnected Windows smoke execution

**Status: tooling implemented; independent physical customer acceptance NOT EXECUTED.**
The owner requires real x86 and x64 disconnected Windows trials, distinct from
GitHub Actions Windows runners. The executable verifier
`build/verify-clean-offline-candidate.ps1` is an **additional, safe automated
subset** that can be run on a disposable external PC or fresh Windows VM.
It never enables or invokes `RestoreDatabaseAtomically`, never creates fake
personnel records in the user's real profile, and never calls the internet.

## Preparation (use separate x86 and x64 clean disposable Windows machines)

1. Obtain the four ACTUAL package binaries and the corresponding two
   `SHA256SUMS.txt` and `BUILD_METADATA.json` files from a **single successful Release workflow** whose
   exact source commit matches both successful Windows Core and Release runs.
   The independent four-file provenance artifact must also be green. Extract
   GitHub Actions *artifact container ZIPs* first: only actual internal customer
   files named below are passed to the script.
2. On an independent **32-bit** Windows installation select these two:
   `AlTamamDaily_<VERSION>_Portable_win-x86.zip` and
   `AlTamamDaily_<VERSION>_Setup_win-x86.exe`, plus the checksum manifest
   attached to Windows-32bit.
   On an independent **64-bit** Windows installation select:
   `AlTamamDaily_<VERSION>_Portable_win-x64.zip` and
   `AlTamamDaily_<VERSION>_Setup_win-x64.exe`, plus the Windows-64bit manifest.
3. Copy the verifier PowerShell file separately from this **same tested source
   commit**, onto the test machine. Use a NEW disposable Windows user/VM with
   **no existing AlTamam database, key, backups or installed AlTamam software**;
   never run a privileged installer test in a real user profile.
4. Physically disconnect or disable **ALL** Windows network adapters before
   execution. The script fails closed if any adapter reports Up or if it cannot
   verify adapter state. Run Windows PowerShell **as Administrator** solely on
   this disposable test VM. No Python, Git, .NET SDK or internet is required.

## Execute the actual packaged binaries

Replace the example candidate version, workflow run ID, source SHA and
file paths with the exact published tested artifacts and record the evidence
outside the temporary test folder. The SHA and run ID here are **examples of
the command format, not universal/current pointers**.

```powershell
$version = "0.0.884-ci"
$arch = "win-x64"  # use win-x86 on a real 32-bit Windows machine
$folder = "D:\OfflineCandidate"
$sourceSha = "8db435853ac03ad9faffc1af4d2f0f805c274c34"
$releaseRunId = "41505775638"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$folder\verify-clean-offline-candidate.ps1" `
  -Version $version -Architecture $arch `
  -PortableZip "$folder\AlTamamDaily_${version}_Portable_${arch}.zip" `
  -SetupExe "$folder\AlTamamDaily_${version}_Setup_${arch}.exe" `
  -Sha256Sums "$folder\SHA256SUMS.txt" `
  -BuildMetadata "$folder\BUILD_METADATA.json" `
  -SourceSha $sourceSha -ReleaseRunId $releaseRunId `
  -ConfirmDisposableWindowsAccount `
  -EvidencePath "$folder\smoke-${arch}.json"
```

The command checks BUILD_METADATA checkout/source SHA, release version, architecture, each byte size and SHA256 against actual bytes. It performs true SHA-256 verification of both binaries; full
Portable extraction/direct execution; real WPF/WebView2 RTL/modal plus SQLCipher
synthetic bridge-role checks; Portable-folder relocation; genuine Inno Setup
install/uninstall/reinstall and AppData preservation. It refuses stale/incorrect
package names, wrong CPU architecture, connected adapters, an existing
AlTamam install/old data, untrusted version, missing evidence path or any
failing Windows process. It does NOT delete real or historical data.
Each PASS or FAIL can produce an evidence JSON, identified by exact run/SHA.
CI can run **only** the parser plus file-integrity negative-path regression;
this does not claim an actual clean offline execution has happened.

`-IntegrityOnly` is available for a non-executing file/hash check and reports
`FILE INTEGRITY ONLY`. It intentionally does not perform any Windows process,
uninstall or independent acceptance test.

## Remaining mandatory independent owner acceptance

Even a green external automated smoke JSON does **not** prove all WIN-01…WIN-10.
An independent operator must still record each relevant manual business
journey (founder/login/roles/Add Person identity/08:00/20:00/monthly report/
Excel/backup/E: fallback), real two-version upgrade, keyboard/RTL/DPI, sustained
10k GUI workload, cold missing-WebView2 installation, real disk-full/power
interruption and separate-account/machine encrypted key recovery. These need
timestamped reproducible logs/screenshots, distinct genuine x86/x64 OS/build,
file SHA-256, exact candidate SHA/run and verifier identity. See
`docs/support/FINAL_VERIFICATION_CHECKLIST.md`.

**Production release is NO-GO** and destructive database restoration stays
disabled until native reauthentication, quiescence, crash rollback and separate
written owner signoff. A CI-green candidate is not a signed final release.
