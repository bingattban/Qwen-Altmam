# AlTamam Release {VERSION} — tested SHA {CHECKOUT_SHA}

**STATUS: UNSIGNED CI RELEASE CANDIDATE — NO-GO** until the exact-SHA
independent four-class clean OFFLINE Windows and native recovery gates pass
and the owner explicitly signs off. This is a TEMPLATE, not a signed release.

## Immutable build identities

- Tested checkout SHA: `{CHECKOUT_SHA}` (on pull_request this can be a
  synthetic GitHub merge ref, NOT the later `main` merge commit)
- Source PR head SHA: `{SOURCE_HEAD_SHA}`
- Windows Core run: {CORE_RUN_URL}, conclusion: {CORE_CONCLUSION}
- Windows Release run: {RELEASE_RUN_URL}, conclusion: {RELEASE_CONCLUSION}
- Windows runner / hardware evidence: {HARDWARE_EVIDENCE}
- Signed binary proof: {SIGNATURE_EVIDENCE_OR_UNSIGNED}
- Manual offline/upgrade/cross-PC acceptance: {ACCEPTANCE_STATUS — NOT_EXECUTED unless independently attached}

## Actual customer artifacts

**Fill ONLY from `SHA256SUMS.txt` and `BUILD_METADATA.json` of the same
tested Release workflow** (never from the outer GitHub artifact ZIP digest).

| Windows class | Real CI file | SHA-256 (fill from run) | Bytes |
|---|---|---|---|
| x86 Portable | `AlTamamDaily_{VERSION}_Portable_win-x86.zip` | {X86_PORTABLE_SHA256} | {X86_PORTABLE_BYTES} |
| x86 Installer | `AlTamamDaily_{VERSION}_Setup_win-x86.exe` | {X86_INSTALLER_SHA256} | {X86_INSTALLER_BYTES} |
| x64 Portable | `AlTamamDaily_{VERSION}_Portable_win-x64.zip` | {X64_PORTABLE_SHA256} | {X64_PORTABLE_BYTES} |
| x64 Installer | `AlTamamDaily_{VERSION}_Setup_win-x64.exe` | {X64_INSTALLER_SHA256} | {X64_INSTALLER_BYTES} |

Check each with PowerShell: `Get-FileHash -Algorithm SHA256 -LiteralPath ".\PACKAGE"`.

## Offline runtime and data policy

- Genuine self-contained `.NET 10` WPF executable with native SQLCipher,
  matching x86/x64 WebView2 loader, Arabic fonts and locally bundled
  license-compliant Microsoft Edge WebView2 offline runtime installer.
  Normal startup NEVER needs internet; missing runtime must be installed
  from the local `Prerequisites` directory. A clean system test is required.
- Both Portable and Installer use exactly
  `%LOCALAPPDATA%\AlTamam` **for the same Windows account**, regardless of
  EXE extraction folder. **There is no adjacent `.\data` mode.**
- Portable runs from the extracted `AlTamam.DesktopHost.exe`. Installer
  uses Inno Setup and preserves per-user DB/key/backups across uninstall.
- The same-user DPAPI key file does NOT automatically transfer to another PC.
  Cross-PC recovery requires separately encrypted portable recovery material
  and independent validation; no silent copy, migration or restore.
- All production resources are local with CSP/virtual-host restrictions;
  the CI packaging step may download an authentic runtime prerequisite
  during build, but the resulting customer package works offline.
- The end-user `RestoreDatabaseAtomically` route remains DISABLED.
  A backup read-only preview is NOT a restored live database.

## Non-final limitations and owner approval

Document actual clean Windows runs for x86 Portable/Installer and x64
Portable/Installer, historical equality/10k multi-year measurement, old→new
upgrade, offline missing runtime, physical disk interruptions, SQLCipher
recovery from independent Windows account, signing/license and P0/P1 triage.

{OWNER_SIGNATURE_OR_PENDING} / {VERIFIER_SIGNATURE_OR_PENDING}
See `docs/release/GO_NO_GO.md`, `docs/release/SECTION11_VERIFICATION_2026-11-02.md` and `docs/support/FINAL_VERIFICATION_CHECKLIST.md`.
