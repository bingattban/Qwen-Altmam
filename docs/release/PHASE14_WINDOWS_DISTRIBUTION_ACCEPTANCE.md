# PHASE 14 — Windows Portable / Installer release-candidate evidence

**Source baseline inspected:** `bingattban/Qwen-Altmam` at
`9e4a32aaca2df6aea847862f7d2a5d7c8a92057a` (main, 2026-11-02).
**Current acceptance:** **PARTIAL / NOT A CUSTOMER RELEASE** until all
independent clean OFFLINE Windows and cross-PC recovery gates execute.

## Existing post-merge blocker and corrective scope

At the inspected main SHA, **both Windows Core and Release FAILED** in
`build/test-monthly-excel-export.js` with
`committed attendance is color coded`:
- Core: https://github.com/bingattban/Qwen-Altmam/actions/runs/41371459843
- Release: https://github.com/bingattban/Qwen-Altmam/actions/runs/41371459846

The previous synthetic monthly test created attendance ONLY on the month's
first day while the genuine UI initially renders a window starting at the
**current** day. The repaired fixture populates both the historical first
day and the first currently visible day. Its detail assertion uses the
visible date; no business code/approved monthly behavior is changed.
New Windows runs must pass; past PR green is NOT proof for new main SHA.

## Reproducible Windows CI packaging gates (each on the same tested checkout)

1. `Windows Core Build` and `Windows Release Packages` start independently
   on the same PR source head SHA. Release preflight/entire C# suite and
   real WPF/WebView2 test MUST succeed **before** either package job runs.
2. Distinct `Windows-32bit`/`win-x86` and
   `Windows-64bit`/`win-x64` jobs publish **self-contained .NET 10 WPF**
   assemblies, local Arabic fonts and OFL license files, native SQLCipher,
   native WebView2Loader, and an Authenticode-validated Microsoft WebView2
   **offline standalone** runtime installer. Downloading the prerequisite
   during an authorized CI build is **not** production runtime downloading.
3. Real target PE architecture of the WPF executable, WebView2Loader,
   hostfxr/hostpolicy and native SQLCipher is validated on the publish tree
   and the extracted customer ZIP. A file with only a familiar filename
   is not sufficient proof of a correct x86/x64 native dependency.
4. Actual extracted Portable ZIP: WPF layout and offline WebView2 navigation,
   followed by the **native HTML/WebView2 -> C# bridge -> synthetic encrypted
   SQLCipher -> HTML** full roundtrip (including all three roles and forbidden
   calls), run for **both win-x86 and win-x64 customer ZIPs**, all with
   exit-code/timeout/trace gates; the **full extracted application folder
   is moved** and the same checks run again. No adjacent data or second key
   path is allowed. Normal offline UI starts directly from the EXE, but
   if WebView2 is not installed the package must install its bundled local
   prerequisite; the missing-runtime case needs separate clean-PC testing.
5. Actual Inno Installer EXE: silent installation, exact WPF EXE SHA-256
   equality with Portable, layout/WebView2 and **real isolated SQLCipher
   bridge/three-role E2E checks** on both installed native architectures,
   **same-version in-place
   reinstallation**, uninstall, reinstall, and AppData sentinel/hash
   preservation. Same-version overwrite is **NOT** proof of a genuine
   older-version-to-new-version upgrade.
6. `build/make-metadata.py` emits real file SHA-256 digests, sizes,
   architecture, `version`, tested checkout `commit` and source
   `sourceHeadSha`. `build/verify-release-artifacts.py` must pass.
   `build/stage-phase14-distributions.py` then streams **byte-identical**
   verified files into `dist/portable`, `dist/installer` and
   `dist/metadata`, checking source/destination hashes.
7. Four separate `actions/upload-artifact` uploads must exist per successful
   Release run: x86 Portable ZIP, x86 Installer EXE, x64 Portable ZIP,
   x64 Installer EXE, plus separate architecture-specific checksum metadata.
   The uploaded GitHub artifact ZIP wrapper SHA does not replace the
   customer-file SHA-256 stored **inside** `SHA256SUMS.txt`.

**AppData identity:** Installer and Portable read
`%LOCALAPPDATA%\\AlTamam` for the same Windows user, including
`altamam.db`, `db.key.protected`, the browser profile and rescue backups.
Neither packaging format chooses adjacent data, moves DPAPI keys, or
migrates data on extraction. Uninstall must not remove this data root,
and independently stored external backups are never uninstalled.

## Independent clean OFFLINE Windows acceptance: mandatory manual matrix

Each row is **NOT EXECUTED** until an actual Windows tester attaches
machine details (Windows edition/build/bitness, CPU/RAM, offline-network
proof), package SHA-256, source SHA, timestamp, evidence and observed result.
CI smoke results are **not substitutes**.

| Environment | Required checks | Current status |
|---|---|---|
| Clean 32-bit Windows, x86 Portable | Extract/direct EXE without app install/elevation for normal use; local WebUI/fonts/SQLCipher; founder setup/login, attendance, XLSX export, backup, logout/restart | NOT EXECUTED |
| Clean 32-bit Windows, x86 Installer | Install, start/login/attendance/export, uninstall retains data, reinstall preserves founder and backup | NOT EXECUTED |
| Clean 64-bit Windows, x64 Portable | Same true offline direct-start and local DB; moved extraction retaining same account data; verify no adjacent key/data | NOT EXECUTED |
| Clean 64-bit Windows, x64 Installer | Same true offline setup, login/attendance/backup/export, uninstall/reinstall without data loss | NOT EXECUTED |
| x86 on compatible x64 Windows | Native loader, SQLCipher, shortcut and per-user AppData without second root | NOT EXECUTED |
| Genuine older-version upgrade | Install **older verified version** then current verified candidate; same database/key/history/retention/schedule before and after | NOT EXECUTED |
| WebView2 absent (fully disconnected) | Bundled signed offline runtime installer works locally, or actionable Arabic remediation; no online bootstrap/download | NOT EXECUTED |
| Out-of-space or ejected E: backup disk | Path error visible; no secret fallback, no loss of existing encrypted backups | NOT EXECUTED |
| Wrong-key and cross-PC key material | Different-PC portable recovery package, independently verified encryption and restore staging; no implicit DPAPI portability | NOT EXECUTED |
| Real power loss and database recovery | Live data remains intact and rescue backups valid; no premature restore activation | NOT EXECUTED |

**Manual test method:** Start from a verified checksum-bound package on
a Windows installation with its network physically disconnected or
interfaces disabled, and record that condition. For missing WebView2,
use a clean machine **without** Evergreen runtime; do not remove a user's
runtime from their existing machine. Use synthetic data, never personnel
production records. Record real application/screenshots and SQLCipher
cipher/version checks; do not use a mocked browser page as evidence.

For actual major upgrade testing use **two immutable versions** and
capture their independent checksums. Running the same installer twice
only proves in-place reinstall safety. Keep externally stored backups
outside all uninstall deletion scopes.

## Non-negotiable acceptance boundaries

- **Do not** mark all four classes fully OFFLINE-accepted based only on
  .NET build, published PE layout, hosted-runner smoke or their presence
  in GitHub's artifacts tab.
- The clean-machine manual workflow intentionally remains a separate
  blocking gate. If a required test has no Windows run/evidence it is
  **NOT EXECUTED**, never PASS.
- The native database restore activation from Phase 12 remains DISABLED
  pending native final confirmation, complete service quiescence, real
  physical power-loss and cross-machine recovery evidence.
- Signatures may be described only if an authorized actual signature
  and certificate are verified. Existing `LICENSE-NOTES.txt` still says
  the final EULA is pending owner supply; **full legal license acceptance
  is NOT VERIFIED** by code-level tests.
- If either Windows workflow, a package test, hash verification, artifact
  upload or another acceptance gate fails, **do not merge**.
