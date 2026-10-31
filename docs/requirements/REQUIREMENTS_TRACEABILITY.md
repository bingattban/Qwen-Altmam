# REQUIREMENTS_TRACEABILITY — Phase 00, reconciled on 2026-10-31

## Source-of-truth and boundary
The owner-approved `AlTamam_Daily_Reconciled_Master_Plan_EN_2026-10-28.txt` (Sections 01–11, Section 07's 69 routes) is the governing directive. `docs/requirements/OWNER_DECISIONS_2026-10-31.md` takes precedence for the newer independent-save, catch-up, screen-preservation and audit-role decisions. Historical audit reports are date-specific snapshots, **not** current requirements, and do not constitute an additional approval/Gate-0 phase. No obsolete Jules/Termux operational prerequisite or fake tool link is authorized.

Source inspected: `bingattban/Qwen-Altmam` `main` @ `62767fef1335ec7da073a9e9d17acc777aa428ac`. Phase 00 is **inventory and reconciliation only**; no Windows runtime, security, final release or service wiring is accepted based solely on files, route contracts or screenshots.

## Current repository inventory, measured
- `AlTamam.slnx`: 5 production C# projects (`AlTamam.Domain`, `Contracts`, `Application`, `Infrastructure`, `DesktopHost`) plus `AlTamam.Tests` xUnit project. `AlTamam.WebUI` is local HTML/CSS/JS assets, not a `.csproj`.
- WPF `.NET 10`, local WebView2, SQLCipher/DPAPI key storage, SQLite history/migrations, native C# bridge. Source architecture is present; actual functional acceptance is owned by later phases.
- Central NuGet versions (from `Directory.Packages.props`): Microsoft.Data.Sqlite.Core 9.0.2; SQLitePCLRaw.bundle_e_sqlcipher 2.1.10; Konscious.Security.Cryptography.Argon2 1.3.1; Microsoft.Web.WebView2 1.0.2903.40; System.Security.Cryptography.ProtectedData 9.0.2; Microsoft.NET.Test.Sdk 17.12.0; xunit 2.9.2; xunit.runner.visualstudio 2.8.2.
- `screen-manifest.json` **actual `screens.length = 69`**, declared count=69, 69 distinct stable IDs and `routeKey`s, 69 equal live `SCREEN_CATALOG` entries (key/path/title/phase), 68 distinct screen HTML files on disk **because screen_070 intentionally shares the Edit Names asset**, plus the root UI shell. Every manifest local asset was found. `action-manifest.json`: 103 registered actions/11 service owners, **not proof of 103 fully tested handlers**.
- Exactly **four** post-login destinations: الرئيسية / التمام الشهري / الإدارة / الإعدادات. First-run, Login, dialogs, exports and scheduler do not add top-level destinations.
- **screen_020 permanently ABSENT**, no duplicate `Reports` route/HTML. Present: `screen_025` sole unified Report; `screen_026` its export; `screen_070` one-person AddPerson; `screen_002` Automatically Prepared; `screen_069` configurable automatic scheduler. Inventory covers screen_001–019 and screen_021–070, not an assumed largest ID.

## Approved navigation, legacy identity and safe returns
- `Administration > Roster Management (إدارة القوة) > Report (التقرير)` opens **screen_025 / PersonnelReport**, the SINGLE individual-or-group chronological report, and **screen_026 / ExportPersonReport** for its local Excel output. No duplicate report under Dismissal.
- `Administration > Roster Management > Edit Names (تعديل الأسماء) > Add Person (إضافة فرد)` opens **screen_070 / AddPerson** as an embedded/modal **single-person** action; Cancel returns to Edit Names. Inspect candidate matches before mutation: genuine SAME HUMAN dismissed/externally transferred return uses the original PersonId; DIFFERENT new human approved to replace a DISMISSED person's business number uses a NEW PersonId, with zero inherited status, movements, attendance, report or audit history. An active-number collision and ambiguous identity must never auto-merge.
- `Dashboard > Automatically Prepared (المحضّرون تلقائياً)` opens read-only **screen_002**; authorized same-day changes use separately confirmed **screen_003**, then return to the register. Saved Absent is not Not Prepared; do not restore the old bulk unprepared dashboard card.
- `screen_069` retains immutable legacy **routeKey=EightPmScheduler** only for route-identity compatibility. Its binding MEANING is default **08:00** daily auto attendance; 20:00 is separate attendance lock. Renaming the key or reviving 20h auto completion is not allowed.

## Original phases 00–15 — preserved ownership
| Phase | Original work ownership |
|---|---|
| 00 | Inventory of requirements/screens/routes; evidence only |
| 01 | Windows .NET/WPF foundation, repeatable Core/Release CI and offline prerequisites |
| 02 | SQLCipher, permanent PersonId, temporal history, indexes and migrations |
| 03 | Founder provisioning, later roster-linked accounts, roles, authentication and sessions |
| 04 | Actual WPF/WebView2 UI, C# bridge and shared-flow integration |
| 05 | Roster, one-person AddPerson, return/replacement, sites and statuses |
| 06 | Attendance, dashboard counters and automatically prepared register/revision |
| 07 | Configurable 08:00 automatic attendance, one-day catch-up, independent 20:00 manual lock |
| 08 | Movements, dismissal and monthly attendance/history |
| 09 | Single person/group Report and working Excel export |
| 10 | Excel import/export and roster-column structure |
| 11 | Immutable audit, visible-only audit revisions/hides and exceptional closed-day correction |
| 12 | Daily 16:00 backup, settings, staged/native restore and reset |
| 13 | Real Windows UI/E2E, security, usability, accessibility and performance |
| 14 | x86/x64 Portable and Installer tests on clean offline Windows |
| 15 | Final security/release evidence, docs, SHA-256, GO/NO-GO |

## Mandatory approved business rules and acceptance scenarios
| Case | Owning phases | Requirement |
|---|---|---|
| Personnel No. (ع) | 02,05,10 | Numeric **TEXT 4–70** digits, Arabic/Persian/Western normalization, preserve leading zeros; PersonId is immutable |
| New vs returning person, ambiguous and active conflict | 02,05,09,13 | SAME human archived return keeps PersonId; DIFFERENT dismissed-number replacement gets NEW PersonId; never blend histories |
| Founding account | 02,03,11 | One founding GeneralAdmin, optional name, permanently **NULL LinkedPersonId**, never linked to a roster human; no second founder |
| Automatic run | 06,07,13 | Default **08:00** configurable automatic TODAY commit using yesterday's approved status or configured active fallback, full provenance, durable ledger/idempotency, no manual overwrites |
| Missed-run handling | 07,13 | Automatic catch-up restricted to **yesterday only** plus eligible today; no older multi-day chained backfill, clock-rollback guard |
| Distinct manual lock | 06,07,11 | Configurable default **20:00** attendance-edit lock; calendar day closes at 00:00; never treat lock as automatic preparation |
| Three setting changes — owner override dated Oct 31 | 07,12,13 | Automatic time, lock time, fallback status saved in **three independent** audited operations; one failure does not roll back successful options and is shown to user |
| Administrative catch-up | 07,11,13 | General and Ordinary Administration only: ONE explicitly selected date within previous **7 calendar days**, preview/reason/commit/idempotency/audit, add missing statuses ONLY; Supervisor denied |
| Transfers after prepared/locked attendance | 08,13 | Authorized movements still valid, preserving committed attendance and effective-dated monthly history |
| Audit presentation changes | 03,11,13 | General and Ordinary Administration can edit/hide visible layer ONLY, Supervisor denied; canonical AuditEvents append-only |
| Encrypted backup | 12,13 | Configurable daily **16:00**, user-visible invalid **E:\AlTamamAlYawmi\Backups** and native folder re-selection, no silent fallback |
| Portable+Installer shared data | 01,12,14 | Both architectures use same per-user **%LOCALAPPDATA%\AlTamam**; moving/extracting/installing/uninstalling must not silently migrate/delete state |
| Safe restore/cross-machine recovery | 02,12,13–15 | E: path failure distinct from key failure; validated encrypted staging, reauth, native confirmation, writer quiescence, atomic cutover/rollback and password-protected recovery on another real Windows machine; destructive restore disabled until verified |
| Four deliverables/test-first gate | 01,14,15 | Core and Release independent PR workflows on exact SHA; Release all required tests BEFORE win-x86/x64 Portable ZIP+Installer EXE; true SHA-256; no merge on failed/missing checks or artifacts |

Optional policies awaiting new owner decision must remain OPTIONAL and must not silently change approved rules. The retired optional 20:00 auto-preparation policy and multi-day auto catch-up are **superseded and prohibited**, not alternative selectable policies. Current screen layout/design is preserved; later phase-owned real service wiring is not confused with visual readiness.

## Precise inventory gaps — no Phase 00 runtime rewrite
- Old `SCREEN_MATRIX.csv` labeled screen_002 «غير المحضرين»; the current manifest and the approved directive call it «المحضّرون تلقائياً». This regenerated matrix derives entry/access/real behavior from Section 07 and preserves every current routeKey.
- `screen_003` manifest breadcrumb still mentions «غير المحضرين»; the approved entry is the Automatically Prepared register protected revision (Phase 06 UI wiring).
- `screen_026` live manifest/catalog title says «تصدير تقرير الفرد», whereas Section 07 calls it «تصدير تقرير الأفراد» for an individual/group export. Both current and approved titles are preserved in matrix columns, without silently changing live UI. Verify group behavior and coordinate any title change in Phase 09.
- `EightPmScheduler` legacy route/folder is a **stable key**, not a 20:00 policy. The old operational document `docs/implementation/PHASE07_20H_RULE.md` is retired as current instructions; source/history is preserved in Git.
- Pre-2026-10-31 audit snapshots, old missing-plan-file/Jules/Termux references and early implementation status must NOT be treated as fresh evidence or additional prerequisites. This does not delete historical audit events or rewrite the original owner-approved phases.

## Phase 00 acceptance/evidence limits
Static validation: Section 07=69 entries; JSON manifest=69; live JS catalog=69; 69 unique keys; all 69 exact stable IDs; 68 HTML physical pages with embedded AddPerson; every manifest path exists; owning phase equality; retired `screen_020`/Reports route and HTML absent; screen_025/070/002/069 correct. This is inventory PASS. Actual .NET and Windows UI/E2E tests were **NOT EXECUTED** by this documentation operation; Phase 01–15 tests, four Windows build artifacts, checksum evidence, native restore and physical UAT remain distinct required gates, never inferred PASS here.
