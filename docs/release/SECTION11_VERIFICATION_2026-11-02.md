# SECTION 11 — Final verification sequence, evidence and delivery standard
Review date: **2026-11-02**. Repository: `bingattban/Qwen-Altmam`.
Baseline: merged `main` commit `2696cd5e15143c4c8a5719878f8bb5162af8089c` (PR #208).
**Decision: CI CANDIDATE ONLY / FINAL PRODUCTION RELEASE NO-GO.**

This is a live-code/evidence reconciliation of owner SECTION 11, not a claim
that every end-to-end interaction, machine or restore test has been physically
executed. The governing precedence is: current owner's express instruction;
`docs/requirements/OWNER_DECISIONS_2026-10-31.md` (plus 2026-11-02 AC-023);
`OWNER_DECISIONS_2026-10-24.md`; reconciled requirements. Prior dated
reports are historical evidence, not permission to bring back obsolete code.

## 11.01–11.16 phase-by-phase verification

| Phase | Live source / test ownership | What has been verified vs remains |
| --- | --- | --- |
| 00 | `REQUIREMENTS_TRACEABILITY.md`, `screen-manifest.json`, `action-manifest.json`, `build/test-phase00-01-inventory.py` | **Source/CI gate:** 69 stable route IDs (001–019,021–070), 103 actions, exactly four HTML sidebar top destinations. `screen_020` absent; `screen_025` sole report; `screen_070` embedded AddPerson. Static validation is NOT all-route manual E2E. |
| 01 | `AlTamam.slnx`, `windows-ci.yml`, `windows-release.yml` | **Executed:** independent Windows Core and Release on PR #208 head `52e245994a855bb637a3f3b39d38d573c493d85a` both green; tests before packaging. Exact new PR/head and merged-main runs still require separate verification. |
| 02 | `AlTamamDbContext.cs`, migrations V001–V013, independent `AlTamam.IntegrationTests` | **Executed synthetic SQLCipher tests** on Windows; permanent PersonId, temporal intervals, foreign keys, WAL, encrypted migration/rescue checks. Live user-data migration and second-PC key recovery NOT accepted. |
| 03 | `Section03FirstRunBridgeTests.cs`, `Section03FoundingAccountTests.cs`, `Section03AccountEligibilityAndLockoutTests.cs`, `build/test-session-idle.js` | **Automated coverage:** founder optional name/always-null LinkedPersonId, admin/supervisor role policy, 5-minute inactivity, logout/new login isolation. Full physical multi-account GUI acceptance PENDING. |
| 04 | `BridgeHandlerCatalog.cs`, `build/test-webview-ui-e2e.ps1` | **Hosted native Windows WPF/WebView2/SQLCipher bridge E2E**, plus offline manifest assertions; full menu/control journey on separate Windows remains PENDING. |
| 05 | `PersonRosterService.cs`, `ArchivedReturnService.cs`, archived-identity integration tests | **Automated identity rules:** single-person AddPerson in Edit Names, SAME human retains original PersonId; different approved human after dismissal gets NEW ID; active/archived ambiguity handled. Real operator report/export cross-check PENDING. |
| 06 | `AttendanceCommitService.cs`, `DailyMonthlyReadService.cs`, `build/test-dashboard-counter-filters.js` | **Automated** preview/correction/status-count/register coverage; real operator UI on physical offline host PENDING. |
| 07 | `AutoAttendanceService.cs`, `AutoAttendancePolicyService.cs`, `AutoAttendanceDefaultPolicyTests.cs` | **Automated** default 08:00 + yesterday-only catch-up and separate 20:00 lock, ledger and fallback; **three independent saves OWNER APPROVED** (not atomic group save). Calendar/offline full operator journey PENDING. |
| 08 | `PersonnelMovementService.cs`, `PersonnelSeparationService.cs`, movement/archives tests | **Automated** internal/external transfer, dismissal and read-only historical archives; physical late-day transfer/attendance/Excel parity PENDING. |
| 09 | `screen_025`, `personnel-report.js`, `PersonnelReportIntegrationTests.cs` | **Automated** single individual/group Report and Excel wiring; independent physical report/export identity parity PENDING; never resurrect `screen_020`. |
| 10 | `RosterColumnStructureService.cs`, Excel import/export tests, monthly UI tests | **Automated** import preview/export and durable ColumnId; real large-file, cancellation, E: failure-path UI acceptance PENDING. |
| 11 | `AuditOverlayService.cs`, `ClosedDayCorrectionIntegrationTests.cs`, `BridgeRbacMatrixTests.cs` | **Automated role/append-only coverage**; both General/Ordinary Administration may edit/hide presentation and correct closed days, Supervisor denied. Physical actor-name historical GUI acceptance PENDING. |
| 12 | `AutomaticBackupService.cs`, backup/recovery/security tests, `BACKUP_RECOVERY_MIGRATION_GUIDE.md` | **Automated** 16:00 scheduling, per-account AppData/Theme, SHA-pinned preview and error policies. **BLOCKED:** user-operated `RestoreDatabaseAtomically` is deliberately disabled; no second-PC power-loss/disk-full/native cutover signoff. |
| 13 | `build/test-webview-ui-e2e.ps1`, Windows test suites, 10k synthetic database diagnostics | **Hosted Windows E2E** passes on earlier exact PR #208 SHA; native Arabic RTL/modal tests and measured SQL query subset. **NOT ACCEPTED:** physical screen reader/1024×768 keyboard, every nested command, full 10k × 31-day UI/Excel acceptance. |
| 14 | `build/test-package-appdata-preservation.ps1`, actual x86/x64 package matrix | **Hosted Windows runners** extract direct-run Portable and install/uninstall/reinstall authentic Inno Installer, verify WebView2, SQLCipher native bridge and identical `%LOCALAPPDATA%\\AlTamam`. **NOT EXECUTED independently:** offline CLEAN x86/x64 customer machines, actual older-to-newer upgrade and missing WebView2 scenario. |
| 15 | `build/verify-phase15-handover.py`, `GO_NO_GO.md`, `PHASE15_ACCEPTANCE_MATRIX.md` | **PR #208 had four real candidate artifacts and independent SHA-256 provenance success.** This Section 11 PR must independently pass at its own SHA after filenames change. Owner handover/signature and external clean-Windows verifier PENDING. |

## 11.17 exact four customer filenames — new incremental correction

Every actual distributable file, SHA256SUMS entry, BUILD_METADATA `files[].file`,
distribution staging and Phase 15 cross-architecture audit must use the EXACT
owner-approved names (GitHub artifact-wrapper directory labels may differ):

```text
AlTamamDaily_<VERSION>_Portable_win-x86.zip
AlTamamDaily_<VERSION>_Setup_win-x86.exe
AlTamamDaily_<VERSION>_Portable_win-x64.zip
AlTamamDaily_<VERSION>_Setup_win-x64.exe
```

`<VERSION>` is replaced by the exact version recorded in verified CI metadata;
for example, a candidate CI version ending in `-ci` is NOT a signed final
production release. Real SHA-256 values can be copied ONLY from newly built,
downloaded, actually hashed customer files on the SAME tested checkout. No
synthetic checksum or artifact is a deliverable; never backfill old package
hashes under new filenames without measuring the resulting bytes.

The approved format is **Inno Setup EXE**, never an unapproved MSI.
The implementation is test-first: `build/test-section11-delivery-standard.py`,
shared `build/release_names.py`, updated metadata/staging/verifiers, and both
Windows Core and Windows Release preflight gates. Release test suite MUST pass
before the Windows x86/x64 customer packages are produced.

## 11.18–11.23 operating and acceptance gates

- Preserve previously functioning handlers, current local source and current
  SQLCipher provider; don't create duplicate actions/Reports engine.
- Every actual failing phase gets a fix and test rerun on a new branch commit;
  no old #84 or #91 wholesale merge: both closed **unmerged**, verified on
  GitHub 2026-11-02.
- Before merging this PR require Core and Release SUCCESS on **exact same PR
  HEAD**, real Release tests **BEFORE** all four packages, two architecture
  checksum artifacts and independent all-four file hash/provenance audit.
  Also require no blocking review, exact base reconciliation and a successful
  merge; re-evaluate both workflows on the new main merge SHA.
- **Do NOT mark CLEAN WINDOWS PASS** merely because GitHub-hosted Windows is
  successful. Human verifier must actually exercise WIN-01…WIN-10 on
  independent network-disconnected Windows, provide OS/CPU/architecture,
  original file and actual SHA256, timestamp, independent screenshots/log,
  real same-account AppData preservation, genuine older→newer upgrade, key
  recovery on a second PC/account, disk-full/power-loss and full UI.
- User-operated encrypted database cutover remains **DISABLED** until native
  reauthentication, one-use staging, stop-and-drain/quiescence, path/digest
  integrity, safe rescue/rollback, real failure acceptance and separate
  written owner signoff. Preview does not equal activation.
- Final production release/tags/signing must remain **NO-GO** until no
  unaccepted critical integrity/security issues and the owner signs off.
  No signed binary or customer-file SHA is asserted here.

**Known current unresolved BUSINESS conflicts:** None newly discovered in
this source reconciliation. The previously disputed combined AC-023 setting
save was resolved by owner in favor of three independent saved operations.
Any newly discovered conflict must be returned to the owner, not silently
decided by software changes.

**Independent verifier:** NOT SUBMITTED.
**Native destructive restore acceptance:** NOT SUBMITTED.
**Owner final customer release signoff:** NOT SUBMITTED.
