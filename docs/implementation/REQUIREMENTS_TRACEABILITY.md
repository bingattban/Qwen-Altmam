# AlTamam — Requirements Traceability Matrix (Phase 00 / Step 00.03)

Baseline commit: `1bb28bb` on branch `qwen-code-3daf4f97-7cb4-4a54-af89-c5f4e69a0b40` (base `main` @ `eb34116`).
All repo counts below were re-verified against the CONNECTED REPOSITORY working tree, not only the supplied project.zip.

## 0. Requirement sources and their status

| Source | Status | Notes |
|---|---|---|
| Owner chat directive sections 1–3 (§2.1–§2.10, PHASE 00 list) | PRESENT (chat transcript; canonical copy requested at `docs/implementation/AlTamam_QwenCoder_Master_Implementation_Prompt_EN.txt`) | **GATE 0 GAP**: master file still absent from repo |
| Canonical external master requirements/step-acceptance archive (README.md refers to it) | MISSING from repository | Requirement-source gap; disputed rules must be owner-approved, never invented |
| `src/AlTamam.WebUI/action-manifest.json` | PRESENT — authoritative action list | 90 actions, 11 owning services, 24 canonical error codes, 13 UI invalidation events, origin `https://manger.adwas.example` |
| `src/AlTamam.WebUI/screen-manifest.json` | PRESENT | 69 declared screens = 69 entries = 69 on-disk `index.html` under `screens/` (+1 root `index.html` shell = 70 HTML files total) |
| `V001_InitialSchema.sql`, Domain entities/policies, Bridge contracts | PRESENT | See AS_IS_ARCHITECTURE.md |

Unverified owner decisions flagged: «الرقم (ع)» uniqueness/search normalization policy beyond `ArabicTextNormalizer.NormalizeForSearch` (file exists and performs non-destructive search normalization — whether it is THE approved canonical policy for service-number uniqueness is awaiting Gate 0); exact semantics of full-database reset and emergency closed-day override; phase numbering of the master plan (screen manifest declares phases 03–13 with per-screen phase tags summing to 69).

## 1. Invariant → mechanism → action → screen → service → test → phase

Legend: Test column = planned regression test id (all NOT YET CREATED; Phase 01+ per §2.4 failing-test-first). Status: IMPL = enforced in code/schema today; PARTIAL = logic drafted, not wired; GAP = not implemented.

| # | Source clause | Business invariant | Enforcement mechanism (current) | Representative actions | Representative screens | Owning service(s) | Planned tests | Phase | Status |
|---|---|---|---|---|---|---|---|---|---|
| 1 | §2.8 | Immutable canonical IDs (PersonId/SiteId/StatusId/AssignmentId/MovementId…) | `StrongIds.cs` value types; DB GUID/PK columns | all mutating actions carry ids | all | all | T-ID-01..03 | 01 | IMPL (types), PARTIAL (no service layer yet) |
| 2 | §2.8 | Display names & «الرقم (ع)» mutable with effective-date snapshots | `PersonIdentityHistory`, `SiteNameHistory`, `AttendanceStatusNameHistory` records + V001 history tables | UpdatePersonNames, RenameSite, EditStatus | EditPersonnelNames, EditSite, EditStatus | PersonRosterService, ConfigurationService | T-HIST-01..04 | 05/08 | IMPL (schema/entities), GAP (services) |
| 3 | §2.8 | «الرقم (ع)» textual key, normalized for search/uniqueness, display preserved | `ArabicTextNormalizer` (non-destructive); raw text kept in display columns | AddPerson, EditPersonDetails, SearchPerson | PersonnelManagement, PersonPickerAndSearch | PersonRosterService | T-NORM-01..05 (incl. Alef/Taa/Yaa/diacritics cases) | 05 | PARTIAL — canonical uniqueness rule UNAPPROVED (Gate 0) |
| 4 | §2.8 | Assignment intervals [start, end) inclusive/exclusive, no overlap | V001 assignment-interval TRIGGERS; `AssignmentPeriodPolicy.cs` | AssignSite, MoveInternal, MoveExternal | Transfers, InternalTransfer, ExternalTransfer | MovementService, ConfigurationService | T-INT-01..06 (boundary day, same-day handoff, backdate) | 07 | IMPL (DB triggers), PARTIAL (policy class unwired) |
| 5 | §2.8 | 0 or 1 committed attendance status per person per local day | V001 uniqueness constraint + triggers; `AttendanceDayPolicy.cs` | CommitAttendance, ReviseAttendance | Attendance, ConfirmAttendance, MonthlyAttendance | AttendanceService | T-ATT-01..05 | 06 | IMPL (DB), PARTIAL (policy unwired) |
| 6 | §2.8 | Transfer effective date prohibits normal attendance; movement+assignment transactionally consistent | Policy intent in `AttendanceDayPolicy`/Movement schema; requiresTransaction flag in manifest | CommitAttendance vs EffectiveDate cross-check | ConfirmAttendance | AttendanceService + MovementService | T-XFER-01..04 | 06/07 | GAP — needs C# use-case enforcement (triggers alone insufficient across tables) |
| 7 | §2.8 | Closed dates not silently writable; audited override workflow | Manifest flags + AuditEvent table; no close-state machine implemented | ClosedDayExceptionalCorrection route actions | ClosedDayExceptionalCorrection, EightPmScheduler | AttendanceService, SettingsBackupService | T-CLOSE-01..05 | 06/13 | GAP |
| 8 | §2.8 | Historical reports resolve names/sites/statuses as-of date; no retroactive snapshot rewrite | History tables designed for as-of resolution; no reporter implemented | GenerateReport, ExportPersonReport | Reports, PersonnelReport, ExportPersonReport | ReportingService, ExcelSheetsService | T-ASOF-01..04 | 09/10 | GAP |
| 9 | §2.8 | Audit evidence immutable; visible edits/deletes are overlays/tombstones | `AuditLogPresentationEdit` entity + V001 table; physical delete forbidden except approved reset | EditAuditEntry, DeleteAuditEntry | AuditLog, EditAuditEntry, DeleteAuditEntry, ModifiedDeletedAuditEntries | UserAuditService | T-AUD-01..06 | 11 | IMPL (schema), GAP (services) |
| 10 | §2.8 | Day/month/archive/dashboard counters & Excel exports derive from same committed SQL facts | Not implemented (dashboards are placeholder JS) | GetDashboardSummary, BuildMonthlyGrid, ExportExcelSheet | Dashboard, MonthlyGrid, SelectArchive, Export* | DashboardService, MonthlyGridService, ExcelSheetsService | T-CNT-01..03 (parity: grid vs report vs export) | 09/12 | GAP |
| 11 | §2.8 | Mutations atomic + request-level idempotency where specified | Manifest: 34/34 mutating actions have `idempotentByRequestId:true`; DbContext serialized-write helper; no dispatcher yet | all mutating actions | all forms | all 11 services | T-IDEM-01..04 (replay, concurrent, crash-mid-tx) | 02/04 | PARTIAL (manifest contract exists; runtime GAP) |
| 12 | §2.8 | Data stays on device; export/backup user-initiated only; no telemetry | Offline-enforced manifest flags (`offlineModeEnforced`, `allowExternalNetwork:false`); JS scan: 0 fetch/XHR/WebSocket/localStorage | CreateBackup, RestoreBackup, ExportExcelSheet | CreateBackup, RestoreBackup, ScheduledBackups | SettingsBackupService, ExcelSheetsService | T-PRIV-01..03 (network egress audit) | 02/10 | IMPL (static), GAP (runtime guard §2.10) |
| 13 | §2.9 | Role hierarchy GA→OA→SV; C# mandatory authorization | `RoleAuthorizationPolicy.cs` (verified source); manifest `allowedRoles` per action (GA 90, OA 88, SV 74 refs, Unauth 2) | CreateUser, DeleteAccount, ResetApplicationData | Users, AddUser, DeleteAccount, ResetApplicationData | AuthenticationService, UserAuditService, SettingsBackupService | T-RBAC 90×3 allow/deny matrix = 270 cases | 04 + per-feature | IMPL (policy fn), GAP (enforcement point = bridge dispatcher) |
| 14 | §2.9 | Dangerous ops (reset/restore/account mgmt/historical correction) need explicit confirmation + structured audit | Confirmation-flow screens exist (9 'confirmation flow' kinds); audit table exists | ResetApplicationData, RestoreBackup, ConfirmDismissal… | ConfirmationAndWarning, ResetApplicationData, DismissPerson flows | SettingsBackupService, PersonRosterService | T-DANGER-01..05 | 11/13 | PARTIAL (UI shells exist; server-side confirm token + audit write GAP) |
| 15 | §2.10 | Argon2id passwords, SQLCipher encryption, DPAPI key protection | `PasswordSecurity.cs` Argon2id (p=2,t=3,m=64MiB,16B salt,32B tag, fixed-time verify); `AlTamamDbContext` SQLCipher 32-byte key | Login, ChangePassword, FirstRunSetup | Login, FirstRunSetup, ChangePassword | AuthenticationService | T-SEC-01..06 | 03 | IMPL (crypto fns), GAP (DPAPI: 0 references found) |
| 16 | §2.10 | WebView2 default-deny navigation to exact virtual host | No WPF/WebView2 host exists | n/a | n/a | Host (future AlTamam.Host project) | T-NAV-01..05 | 02 | GAP |
| 17 | §2.8/manifest | 20:00 auto-attendance carry-forward + startup catch-up ledger | V001 `AutoAttendanceRuns` table (line ~318); `ISystemClock` abstraction; EightPmScheduler screen | RunScheduledAttendance-type actions (manifest) | EightPmScheduler | AttendanceService | T-SCHED-01..05 (clock change, DST, offline overnight) | 13 | GAP (table only; no scheduler) |

## 2. Action ↔ screen ↔ service coverage checks (repo-verified)

- 90 actions ↔ 11 services distribution (verified by script): Attendance 7, Auth 6, Configuration 12, Dashboard 4, ExcelSheets 11, MonthlyGrid 3, Movement 11, PersonRoster 7, Reporting 6, SettingsBackup 13, UserAudit 10. Sum = 90 ✔
- Every action has complete fields: `action, owningService, allowedRoles, isMutating, idempotentByRequestId, errorCodes, requestSchema` ✔; every referenced errorCode ∈ the 24 canonical codes ✔
- 69 screens: all `localAssetPath`s exist on disk (0 mismatches) ✔; 70 `data-bridge-action` markers in HTML await real binding (placeholder `screen-module.js` refuses business actions — honest state)
- 13 uiInvalidationEvents defined; no C# publisher exists yet (GAP until bridge step)
- Screen manifest declares implementationPhase 03–13 (6+6+12+5+1+12+4+10+5+6+2 = 69) — these are the CANONICAL per-screen phase tags; the master prompt's own phase list must reconcile with them at Gate 0.

## 2b. Per-action coverage marking (directive §20, 2026-10-23)

Moved to dedicated document: **`docs/implementation/MANIFEST_ACTION_COVERAGE.md`** — all 90 actions × (kind, idempotency, implementation status, test marking) generated mechanically from `action-manifest.json` + `BridgeHandlerCatalog.cs` switch arms and cross-checked against the §20 inventory with zero mismatches. Headline: 19 WIRED / 71 honest REFUSAL (phase-pending); RBAC matrix authored for 90×4=360 cases; execution status remains NOT RUN (no dotnet in sandbox) — no row was upgraded to PASS on the strength of authoring alone.

## 3. Explicit gaps blocking full traceability

1. Master requirements text absent from repo (Gate 0) → clauses "canonical policy", "approved matrix", "as specified" cannot be fully cited.
2. No Application-service classes exist for any of the 90 actions → action→code rows remain PENDING.
3. No tests exist → every Test id above is a plan, not evidence.
4. Repository URL/base branch unconfirmed by owner (sandbox has no remote).
