# SECTION 09–10 — GitHub source reconciliation and real acceptance register
**Repository:** bingattban/Qwen-Altmam, baseline main `88fb153b0fb278aac8be578eef835867642f02db`, 2026-11-02.
**Status: CANDIDATE / NOT A RELEASE GO.** This source map is NOT proof that all 46 end-to-end scenarios have been executed. Real clean-offline Windows, independent x86/x64 installation, power interruption, second-PC recovery, WPF/keyboard accessibility and owner final signoff require separately retained measurements.

## SECTION 09 — SQLCipher current table mapping and incremental constraints
The owner's desired singular table names map to the existing physical `Persons`, `PersonIdentityHistory`, `Sites`, `SiteNameHistory`, `AttendanceStatusDefinitions`, `PersonAssignments`, `AttendanceRecords`, `AttendanceRevisions`, `PersonnelMovements`, `Users`, `AuditEvents`, `AuditLogPresentationEdits`, `CustomFieldDefinitions`, `CustomFieldValues`, `AppSettings`, `BackupHistory` and `AutoAttendanceRuns`. **Do not rename these live tables:** working services and historical SQLCipher migration pins depend on them. New V013 is forward-only and adds no data-copy or table-renaming operation.

| Clause | Existing mapping / incremental rule | Primary source | State |
|---|---|---|---|
| 09.01 | PersonId permanent, no physical delete | `src/AlTamam.Infrastructure/Persistence/Migrations/V007_PermanentIdentifiers.sql` | Source mapped; full acceptance pending |
| 09.02 | Canonical 4–70 digit personnel number; temporal identity | `src/AlTamam.Infrastructure/Persistence/Migrations/V004_HistoricalIntegrity.sql` | Source mapped; full acceptance pending |
| 09.03 | SiteId/name history independent | `src/AlTamam.Infrastructure/Persistence/Migrations/V004_HistoricalIntegrity.sql` | Source mapped; full acceptance pending |
| 09.04 | StatusCatalog historical row retention | `src/AlTamam.Infrastructure/Persistence/Migrations/V007_PermanentIdentifiers.sql` | Source mapped; full acceptance pending |
| 09.05 | No assignment overlap; unique open interval | `src/AlTamam.Infrastructure/Persistence/Migrations/V013_MovementAttendanceImmutableKeys.sql` | V013 candidate, needs live Windows CI |
| 09.06 | One attendance status per person/day; immutable person/date | `src/AlTamam.Infrastructure/Persistence/Migrations/V013_MovementAttendanceImmutableKeys.sql` | V013 candidate, needs live Windows CI |
| 09.07 | Append-only AttendanceRevisions | `src/AlTamam.Infrastructure/Persistence/Migrations/V004_HistoricalIntegrity.sql` | Source mapped; full acceptance pending |
| 09.08 | Movement type 1–4; Return only same human | `src/AlTamam.Infrastructure/Persistence/Migrations/V013_MovementAttendanceImmutableKeys.sql` | V013 candidate, needs live Windows CI |
| 09.09 | Founder independent, one immutable founding role | `src/AlTamam.Infrastructure/Persistence/Migrations/V011_PermanentFoundingRoleAndAccountRoster.sql` | Source mapped; full acceptance pending |
| 09.10 | Canonical AuditEvents append-only | `src/AlTamam.Infrastructure/Persistence/Migrations/V004_HistoricalIntegrity.sql` | Source mapped; full acceptance pending |
| 09.11 | Immutable overlay audit-display revisions | `src/AlTamam.Infrastructure/Persistence/Migrations/V005_AuditPresentationEvidence.sql` | Source mapped; full acceptance pending |
| 09.12 | Custom field definitions and person-scoped values | `src/AlTamam.Infrastructure/Persistence/Migrations/V001_InitialSchema.sql` | Source mapped; full acceptance pending |
| 09.13 | AppSettings and encrypted BackupHistory | `src/AlTamam.Infrastructure/Persistence/Migrations/V001_InitialSchema.sql` | Source mapped; full acceptance pending |
| 09.14 | Synthetic tests, FK, WAL, one-writer and online backup | `tests/AlTamam.Tests/Persistence/SchemaInvariantTests.cs` | Source mapped; full acceptance pending |
| 09.15 | Temporal triggers + service verification + real SQLCipher tests | `tests/AlTamam.Tests/Persistence/Section02HistoricalIntegrityTests.cs` | Source mapped; full acceptance pending |
| 09.16 | Idempotent 08:00 ledger preserves old 20:00 facts | `src/AlTamam.Application/Scheduling/AutoAttendanceService.cs` | Source mapped; full acceptance pending |
| 09.17 | Shared per-user AppData across Portable/Installer | `src/AlTamam.Infrastructure/Persistence/AppDataLayout.cs` | Source mapped; full acceptance pending |
| 09.18 | Read-only restore staging; native atomic restore remains disabled | `src/AlTamam.Infrastructure/Persistence/EncryptedRestoreStagingService.cs` | Source mapped; full acceptance pending |

## SECTION 10 — 46 non-omittable scenarios
The path below is **related implementation/test evidence**, not a claim that that file independently proves the full requested end-to-end scenario. Every AC requires real acceptance before production GO. `NOT_EXECUTED` means independent acceptance has not been provided. `AUTO_PARTIAL` means related automated source/tests exist, but the complete AC remains unaccepted.

| Scenario | Required observed behavior | Related source/test | Full E2E acceptance |
|---|---|---|---|
| AC-001 | First-use GeneralAdmin and second setup rejected | `tests/AlTamam.Tests/Bridge/Section03FirstRunBridgeTests.cs` | AUTO_PARTIAL |
| AC-002 | Founder full name optional; LinkedPersonId permanently NULL | `tests/AlTamam.Tests/Bridge/Section03FoundingAccountTests.cs` | AUTO_PARTIAL |
| AC-003 | Two administrator levels and Supervisor denial | `tests/AlTamam.Tests/Bridge/BridgeRbacMatrixTests.cs` | AUTO_PARTIAL |
| AC-004 | 4:59 active versus 5:00 idle lock; drafts isolated | `build/test-session-idle.js` | AUTO_PARTIAL |
| AC-005 | Logout/login session isolation and in-flight denial | `tests/AlTamam.Tests/Bridge/Section03InFlightRevocationTests.cs` | AUTO_PARTIAL |
| AC-006 | Add single person via Edit Names | `tests/AlTamam.Tests/Bridge/Phase05RosterSecurityAcceptanceTests.cs` | AUTO_PARTIAL |
| AC-007 | Active duplicate number rejected before database write | `tests/AlTamam.Tests/Bridge/Phase05RosterSecurityAcceptanceTests.cs` | AUTO_PARTIAL |
| AC-008 | Dismissed same-human Return versus different-new-PersonId | `tests/AlTamam.Tests/Bridge/ArchivedIdentityReturnIntegrationTests.cs` | AUTO_PARTIAL |
| AC-009 | External transfer: verified return and different-person refusal | `tests/AlTamam.Tests/Bridge/ArchivedIdentityReturnIntegrationTests.cs` | AUTO_PARTIAL |
| AC-010 | Similar-name candidate disambiguation, no auto-merge | `tests/AlTamam.Tests/Bridge/ArchivedIdentityReturnIntegrationTests.cs` | AUTO_PARTIAL |
| AC-011 | Mid-month admission leaves prior dates blank | `tests/AlTamam.Tests/Bridge/DailyMonthlyReadTests.cs` | AUTO_PARTIAL |
| AC-012 | Today attendance revision retains previous/new and actor | `tests/AlTamam.Tests/Bridge/AttendanceCommitIntegrationTests.cs` | AUTO_PARTIAL |
| AC-013 | Ordinary yesterday edit denied, admin correction requires reason | `tests/AlTamam.Tests/Bridge/ClosedDayCorrectionIntegrationTests.cs` | AUTO_PARTIAL |
| AC-014 | Historical audit actor roster-name snapshot remains stable | `tests/AlTamam.Tests/Persistence/Section02HistoricalIntegrityTests.cs` | AUTO_PARTIAL |
| AC-015 | Admin audit-display edit appends genuine meta-audit | `tests/AlTamam.Tests/Bridge/Phase11AuditHistoryAcceptanceTests.cs` | AUTO_PARTIAL |
| AC-016 | GeneralAdmin audit tombstone but no canonical deletion | `tests/AlTamam.Tests/Bridge/Phase11AuditHistoryAcceptanceTests.cs` | AUTO_PARTIAL |
| AC-017 | Transfer after attendance/lock preserves date history | `tests/AlTamam.Tests/Bridge/PersonnelMovementIntegrationTests.cs` | AUTO_PARTIAL |
| AC-018 | External and dismissal archives preserve independent history | `tests/AlTamam.Tests/Bridge/PersonnelSeparationIntegrationTests.cs` | AUTO_PARTIAL |
| AC-019 | Unique screen_025 report one/group and Excel | `tests/AlTamam.Tests/Bridge/PersonnelReportIntegrationTests.cs` | AUTO_PARTIAL |
| AC-020 | Identical names retain distinct PersonId and effective sites | `tests/AlTamam.Tests/Bridge/MonthlyHistoryReuseTests.cs` | AUTO_PARTIAL |
| AC-021 | All-sites monthly export plus per-site sheets/path error | `tests/AlTamam.Tests/Bridge/MonthlyExcelExportTests.cs` | AUTO_PARTIAL |
| AC-022 | Renaming column retains ColumnId and history | `tests/AlTamam.Tests/Bridge/RosterColumnStructureIntegrationTests.cs` | AUTO_PARTIAL |
| AC-023 | 16:00 backup; independently saved 08:00/20:00/fallback | `tests/AlTamam.Tests/Bridge/AdminSettingsIntegrationTests.cs` | OWNER_CONFLICT |
| AC-024 | Missing disk shows exact Arabic error, no false success | `tests/AlTamam.Tests/Bridge/Phase12BackupDiskRecoveryTests.cs` | AUTO_PARTIAL |
| AC-025 | Lost key versus disk errors; protected second-PC package | `tests/AlTamam.Tests/Security/PortableRecoveryKeyPackageTests.cs` | NOT_EXECUTED |
| AC-026 | Corrupt backup/wrong key leaves live database intact | `tests/AlTamam.Tests/Persistence/StagedRestorePreparationTests.cs` | AUTO_PARTIAL |
| AC-027 | Four real offline x86/x64 Portable/Installer distributions | `build/test-package-appdata-preservation.ps1` | NOT_EXECUTED |
| AC-028 | 10k × 31-day virtualized RTL monthly grid | `build/test-table-experience.js` | NOT_EXECUTED |
| AC-029 | Idempotent movement/bulk/auto and old 20:00 ledger facts | `tests/AlTamam.Tests/Bridge/Section02MutationReplayTests.cs` | AUTO_PARTIAL |
| AC-030 | Absent counted as actual Absent, not Unprepared | `build/test-dashboard-counter-filters.js` | AUTO_PARTIAL |
| AC-031 | Departed person's account disabled; old attribution retained | `tests/AlTamam.Tests/Bridge/Section03AccountEligibilityAndLockoutTests.cs` | AUTO_PARTIAL |
| AC-032 | No external network/files; local WebView2 and fonts | `build/manifest-guard.py` | AUTO_PARTIAL |
| AC-033 | Success only after committed transaction/file write | `tests/AlTamam.Tests/Bridge/Section08ResponseEnvelopeTests.cs` | AUTO_PARTIAL |
| AC-034 | Exact SHA parallel Core/Release then four artifacts and audit | `build/verify-phase15-handover.py` | AUTO_PARTIAL |
| AC-035 | Canonical four to seventy digits, leading zeros, no PersonId mixing | `tests/AlTamam.Tests/Bridge/ExcelNumericServiceNumberTests.cs` | AUTO_PARTIAL |
| AC-036 | 08:00 carry-forward/default; 20:00 separate lock | `tests/AlTamam.Tests/Bridge/AutoAttendanceDefaultPolicyTests.cs` | AUTO_PARTIAL |
| AC-037 | Auto prepared readonly, separate preview/confirmation/audit | `build/test-readonly-route-states.js` | AUTO_PARTIAL |
| AC-038 | Different newcomer historical separation in reports/export | `tests/AlTamam.Tests/Bridge/MonthlyHistoryReuseTests.cs` | AUTO_PARTIAL |
| AC-039 | Founder's roster link cannot be forged or assigned later | `tests/AlTamam.Tests/Bridge/Section03FoundingAccountTests.cs` | AUTO_PARTIAL |
| AC-040 | Superseded PR #84/#91 not wholesale reapplied | `docs/release/GO_NO_GO.md` | AUTO_PARTIAL |
| AC-041 | Session-owned progress and safe workbook cancellation | `tests/AlTamam.Tests/Bridge/ExportJobCoordinatorTests.cs` | AUTO_PARTIAL |
| AC-042 | Persisted Theme key restores without duplicate engine | `tests/AlTamam.Tests/Bridge/ThemeSettingsTests.cs` | AUTO_PARTIAL |
| AC-043 | Missing E: requires writable re-selection; live DB unchanged | `tests/AlTamam.Tests/Bridge/Phase12BackupDiskRecoveryTests.cs` | AUTO_PARTIAL |
| AC-044 | Restore preview/stage != activation; offline recovery gate | `tests/AlTamam.Tests/Bridge/NativeRestoreReauthenticationGateTests.cs` | NOT_EXECUTED |
| AC-045 | Three separate movement/dismissal archives and non-destructive history | `build/test-personnel-movements.js` | AUTO_PARTIAL |
| AC-046 | 1024x768 RTL/170x35 search/sticky grid/modal keyboard focus | `build/test-modal-first-design.js` | NOT_EXECUTED |

## Owner ruling / acceptance boundaries
- **AC-023 OWNER_CONFLICT:** the submitted text says three attendance settings are saved atomically together, but the owner's later approved rule requires **three independent audited saves**: 08:00 automatic time, 20:00 lock, fallback status. Preserve the later approved independent-save behavior; no partial success is rolled back and any failure is reported. Backup time 16:00 remains a fourth independent setting. A change to the owner rule requires an explicit decision.
- **AC-027/028/044/046 NOT_EXECUTED:** Github-hosted Windows E2E/smoke or synthetic benchmarks do not prove a separate clean offline x86/x64 PC, physical 10k grid scrolling, atomic cutover after power loss, or all RTL keyboard/focus requirements. Do not activate the disabled destructive restore path.
- **AC-025:** protected recovery must be demonstrated on a second actual Windows device/account. Never log raw SQLCipher keys.
- **AC-034:** a green PR workflow produces candidate packages, NOT production approval; recheck the merged-main SHA and require manual GO/NO-GO signoff.
- **Migration V013:** new triggers prevent unknown movement types and changes to a committed attendance's PersonId/date; an additional partial unique index forbids more than one open assignment for any person. Existing V001–V012 SQL, migration-name/hash pins, legacy 20:00 execution rows, DPAPI keys and live file layout are unchanged.
- **Gates:** both independent Core/Release workflows must pass on the SAME final PR HEAD; all Release tests BEFORE x86/x64 Portable/Installer; verify all four bytes, SHA-256 and source provenance; merge only after all green. Main push workflows must then finish separately. Any failure = NO MERGE.
