# AlTamam Release {VERSION} — build {COMMIT} ({BUILT_UTC})

**Status: RELEASE CANDIDATE.** Publication is blocked until the Phase 16 gate passes and the
owner explicitly approves (directive 15.08). Nothing in this file may claim "secure" for an
unsigned build.

## Artifacts (both produced by workflow `Windows Release Packages (Phase 15)`)
| Artifact | SHA-256 | Size |
|---|---|---|
| AlTamam-Portable-win-x64.zip | {SHA_PORTABLE} | {BYTES_PORTABLE} |
| AlTamam-Installer-win-x64.exe | {SHA_INSTALLER} | {BYTES_INSTALLER} |

Verify before use: `Get-FileHash -Algorithm SHA256 <file>` must equal SHA256SUMS.txt from the same run.

## Signing
Signing: **{SIGNED_OR_UNSIGNED}**. Unsigned candidates carry NO security endorsement; SmartScreen/AV
prompts are expected and are not bypassed by renaming or fake manifests. Optional signing runs only
with an owner-provisioned approved certificate stored in repository secrets (never in the repo).

## Contents / runtime strategy (15.05)
- Genuine WPF executable `AlTamam.DesktopHost.exe` (.NET 10 self-contained, win-x64, layout validated
  by build/validate-layout.py in CI: real PE header check, WebView2Loader.dll, native SQLCipher under runtimes/).
- Bundled offline WebUI (69 screens) served from pinned virtual origin https://manger.adwas.example
  via WebView2 folder mapping (host-resource access = Deny); strict CSP, no external network.
- WebView2 Evergreen runtime required: detected at first run; if absent the app shows a bilingual
  prerequisite message. Offline-bundled fixed-version runtime is an OPEN OWNER DECISION — do not ship
  until decided. Minimum OS: Windows 10 20H2+ x64 (WebView2 SDK baseline; recorded as untested on older builds).

## Installer policy (15.04)
Inno Setup 6, admin install to Program Files, per-user writable data in %LOCALAPPDATA%\AlTamam
(DB + backups + DPAPI-protected key file). Uninstall NEVER deletes user data; upgrade-in-place
preserves it. Portable ZIP stores data in .\data beside the EXE.

## Tested on
CI matrix evidence links: {RUN_URL}. Clean-machine acceptance (15.06): {ACCEPTANCE_STATUS —
PENDING until logged with run URL; no pass claimed without evidence}.

## Known limitations carried into Phase 16
- UI smoke beyond --layout-selfcheck requires interactive session tests (owner-run checklist).
- License report generation step pending owner choice of tooling.
