# Section 01 — fixed technical boundaries: implementation/evidence ledger

**Owner directive:** Section 01.01–01.13, received 2026-10-29.
**Audited baseline `main`:** `090ffe65010d57f3d3f5ce95072ac0bd43784fb1`.
**Authority:** latest owner instructions > approved written decisions > owner-reconciled master plan > compatible earlier concepts.
**Acceptance rule:** this document is a source audit and work record; it is NOT a final production acceptance. Passing CI and shipping release candidates is not independent offline user acceptance.

| Requirement | Verified implementation / limitation |
| --- | --- |
| 01.01 | Personnel, daily/monthly history, movements, archives, reports, role accounts, Excel, encryption, backup and audit services exist. Destructive restore remains intentionally disabled pending complete native safety and owner acceptance; numerous separate route HTML files are still generic shells even though the main connected UI exposes real workflows. **PARTIAL.** |
| 01.02 | Single local PC, independent role-based account logins, server-side session issuance/revocation, serialized SQLCipher transaction writes and a native single-instance `Local\\AlTamamDaily_DesktopHost` mutex within the current Windows session. Cross-Windows-session exclusion has not been independently demonstrated. **PARTIAL VERIFICATION.** |
| 01.03 | Offline WebView2 runtime installer is bundled with both architectures; local UI virtual-origin mapping and cross-origin network denial are present. Real network-disconnected clean-PC first-run/login/import/restore smoke is a remaining manual acceptance gate. **PARTIAL VERIFICATION.** |
| 01.04 | `<html lang="ar" dir="rtl">`, locally staged Tajawal/Cairo, Arabic normalization and ASCII-canonicalized service numbers preserving leading zeros. **IMPLEMENTED; functional parity testing ongoing.** |
| 01.05 | Exactly four primary post-login buttons (`الرئيسية`, `التمام الشهري`, `الإدارة`, `الإعدادات`) enforced by UI guard; login/init precede navigation. **IMPLEMENTED.** |
| 01.06 | Genuine .NET 10 WPF/WebView2 shell and C# Application/Domain/SQLCipher Infrastructure. This change binds native startup visibility/error text to an observable WPF `StartupViewModel`, retaining startup concurrency and offline WebView2 initialization. **IMPLEMENTED FOR HOST STARTUP; remaining workflow completeness is tracked elsewhere.** |
| 01.07 | Installer and Portable share per-Windows-user `%LOCALAPPDATA%\\AlTamam` for encrypted DB, DPAPI key, WebView2 and internal rescue. No executable-path-triggered migrations. **FIX:** Reading Settings no longer creates an `E:\\AlTamamAlYawmi\\Backups` directory. Missing/unwritable preferred or persisted paths remain visible and unusable until the user explicitly selects a local directory through the existing Windows folder picker. Mapped network drives are rejected; manual backup validates writability up front. **IMPLEMENTED, pending exact-SHA CI.** |
| 01.08 | A synthetic 10,000-person search benchmark exists. The complete five thresholds (warm startup <3s, routine navigation <300ms, 10k search <200ms, 1k attendance commit <3s, smooth 10k × 31 scroll) have NOT been measured end-to-end on a documented ordinary Windows SSD PC. **OPEN PERFORMANCE ACCEPTANCE, not a guarantee.** |
| 01.09 | Typed C# bridge dispatch, server-side RBAC, transaction write gate and SQLCipher access confined to C#; JS invokes typed actions only. Re-run security regressions after every changed SHA. **IMPLEMENTED; end-to-end abuse assessment remains part of release.** |
| 01.10 | Repository ignore/CI guards cover DB, keys, Excel and PDF artifacts; CI fixtures are synthetic. Never upload actual records, keys, production exports or logs. **IMPLEMENTED PROCESS GATE.** |
| 01.11 | Windows Core and Release are independent PR/main workflows; Release's packaging job depends on successful full-suite Windows tests; each architecture uploads real Portable ZIP, Installer EXE and SHA-256 metadata. Both must pass at the exact PR SHA before merge and be rechecked at the merge SHA. **IMPLEMENTED CI CONTRACT.** |
| 01.12 | Shared table code supplies table-scoped search, existing large-table pagination, full-height responsive grids, and standardized 170 × 35 CSS pixel search; accessibility and modal tests exist. Route-specific placeholder HTML remains a separate functional-integration gap. **PARTIAL.** |
| 01.13 | The existing approved `src/AlTamam.DesktopHost/Assets/AlTamam.ico` is referenced in the WPF host and installer. This task does NOT change or replace it. **PRESERVED.** |

## Test-first change record

Tests and the static MVVM guard were committed to a dedicated review branch **before** corresponding implementation changes. Changes are limited to:

1. `tests/AlTamam.Tests/Bridge/IdleAndSharedOutputTests.cs`: no automatic E: folder creation, no fallback or false-positive backup if selected storage disappears.
2. `build/validate-ui-shell.py`: require observable native startup model and XAML bindings, not UI element mutations.
3. `src/AlTamam.DesktopHost/Presentation/StartupViewModel.cs` (new): `INotifyPropertyChanged` with Arabic startup text and panel visibility.
4. `src/AlTamam.DesktopHost/MainWindow.xaml` and `MainWindow.xaml.cs`: UI binding only; SQLCipher, scheduler, navigation and icon preserved.
5. `src/AlTamam.Application/Bridge/OutputDirectorySettings.cs`: no implicit output-folder creation; reject unavailable/network paths; preserve stored choice.
6. `src/AlTamam.Application/Bridge/SettingsReadBackupService.cs`: explicit writable-folder probe and in-app action guidance before manual backup.

**Required result:** Windows Core + Windows Release PASS on the SAME PR head, no missing tests, no failed packaging jobs, four CI packages with checksum files, then merge and post-merge CI on the new `main` SHA. Record run URLs in PR and do not infer release signoff from green CI. Performance/hardware gates and complete offline clean-machine acceptance are intentionally still OPEN. Destructive restore remains disabled.

**Conflict requiring new owner policy?** None introduced by these non-destructive fixes. An unapproved historic `Phase 16` numbering convention remains separate from the owner's approved phases `00–15`; no renumbering or policy change is made here.
