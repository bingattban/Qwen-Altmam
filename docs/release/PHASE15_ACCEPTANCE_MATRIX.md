# Phase 15 executable evidence map — incremental handover

**Scope:** approved PHASE 15 ONLY. No reopening closed PR #84/#91 or resurrecting retired screen_020. No change to approved two-admin audit-display/closed-day policy.

## Verified current repository inventories (main `c599b9aae191be36cf1ef72193719f2ca2182d60`)
- 69 canonical routes in `src/AlTamam.WebUI/screen-manifest.json`, including unique embedded `screen_070` AddPerson (hosted by EditPersonnelNames) and `screen_025` individual/group PersonnelReport. `screen_020` absent from manifest.
- 103 canonical bridge actions; `UpdateAuditPresentation`, `DeleteAuditPresentation`, `PreviewClosedDayCorrection`, `CommitClosedDayCorrection`, `PreviewAdministrativeAttendanceCatchUp` and `CommitAdministrativeAttendanceCatchUp` are restricted to General/Ordinary Administration in manifest.
- `build/test-personnel-report-ui.js` demonstrates a mocked UI report selection: OLD-ID/NEW-ID with the same visible service number remain individually addressed by immutable PersonId. This alone is **NOT** actual production SQL/Excel and real Windows acceptance.
- C# `ArchivedIdentityReturnIntegrationTests`, `BridgeRbacMatrixTests`, `Phase07CalendarClosureTests`, `NativeRestoreReauthenticationGateTests` and other tests exercise synthetic SQLCipher/bridge contracts on Windows runner. Individual cases need per-test evidence to claim complete Phase 15 scenario coverage.
- A new `build/test-phase15-handover-contract.py` is deliberately **STATIC** (routes/permission manifest/handover artifacts). It must run and pass in both workflows but cannot substitute real E2E.

## Independent real-Windows acceptance record (fill only after execution)
| Acceptance ID | Platform, scope and expected evidence | Status |
| --- | --- | --- |
| WIN-01 | Clean offline Win32; x86 Portable direct EXE; full actual first-run, all role workflows, local SQLCipher, backup/Excel; SHA/log | NOT EXECUTED |
| WIN-02 | Clean offline Win32; x86 Installer install/use/uninstall/reinstall; same AppData root and complete before/after hashes | NOT EXECUTED |
| WIN-03 | Clean offline Win64; x64 Portable moved folder; same per-user data, no adjacent key, full UI | NOT EXECUTED |
| WIN-04 | Clean offline Win64; x64 Installer + missing WebView2 prerequisite + offline independent run | NOT EXECUTED |
| WIN-05 | Genuine older-to-newer update with two version SHAs, DB/key/audit/attendance persistence | NOT EXECUTED |
| WIN-06 | Physical disk-full/power interruption, encrypted rescue, WAL/journal replay and no data loss | NOT EXECUTED |
| WIN-07 | Second offline PC/account, valid/wrong key package, tampered intent, native reauth, recovery | NOT EXECUTED |
| WIN-08 | E: folder missing/unwritable; in-UI user-visible re-selection and retry; export/backup proof | NOT EXECUTED |
| WIN-09 | 10,000 people × multiple YEARS: exact per-operation measured timings and parity for daily/monthly, history, report and Excel, full configuration | NOT EXECUTED |
| WIN-10 | Every essential role/command, AddPerson, Individual/Group Report, transfer after attendance, 08:00 vs 20:00 lock, audit snapshots | NOT EXECUTED |

Each completed manual record must include: immutable tested checkout/source SHA; Windows edition/build/x86 or x64, CPU/RAM/storage, fully offline verification; distribution name and actual SHA-256; precise commands/screens and observed state (no real personnel PII); timestamp, log/screenshots; pass/fail and independent verifier identity.

**Known current performance scope:** the `TenThousandSearchDiagnostics` fixture runs 10,000 personnel **search only** and writes actual runner data in `section01-search-10k-perf-windows` under Core. The `SqliteIndexPerformanceFixture` seeds 2,000 persons × six month-periods, not 10,000 multi-year attendance. No invented benchmark number is entered here.

**Release state: NO-GO.** A passing static manifest test, all-green hosted runner CI, or an uploaded EXE is not an independent production signoff. See `GO_NO_GO.md`.
