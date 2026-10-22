# AlTamam — Manifest Action Coverage Checklist (90 actions / 11 services)

Directive §20 completeness checklist. **Authoritative source:** `src/AlTamam.WebUI/action-manifest.json`
(exact signatures, allowedRoles, requestSchema, mutating/idempotency, error codes). This document marks
implementation and test status per action; it does not replace the manifest.

Generated & cross-checked 2026-10-23 from working tree at local HEAD `2d0be83`:
- Inventory ↔ manifest cross-check: **PASS** — all 90 names present, owningService matches on every row, no extras.
- C# mirror (`ActionManifest.cs`): 90 records, enforced equal by `BridgeRbacMatrixTests.ManifestMirror_ContainsExactlyTheDeclaredNinetyActions`.
- Dispatcher registration: enforced by `BridgeRbacMatrixTests.Dispatcher_RegistersEveryManifestAction_NoUiOnlyGating`.

## Status vocabulary (honest, per §2.3 — never translate absent coverage into PASS)

| Code | Meaning |
|---|---|
| WIRED | Authentic handler executes real logic through the bridge dispatcher |
| REFUSAL | Registered with full manifest metadata; returns honest phase-pending refusal (never fake success) |
| RBAC ✔ | Authorization gate exercised by the 360-case matrix (90 actions × GA/OA/SV/Unauthenticated), server-side, independent of UI visibility |
| FUZZ ✔ | Payload path exercised by hostile-input fuzz tests |
| FT — | No feature-level (behavioral) test yet; only contract/RBAC coverage exists |

Totals: 90 actions / 11 services — WIRED: 19, REFUSAL (phase-pending): 71.
All 90 are RBAC-gated at the dispatcher and covered by the matrix; behavioral wiring follows the phase plan.

## AttendanceService (7 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| GetActiveAttendanceStatuses | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewAttendanceChange | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| SubmitAttendance | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| ReviseTodayAttendance | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| SaveAttendanceDraft | MUTATION | yes | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: ✔; FT: BridgeFuzzPayloadTests.HostileDraftFields_* |
| ListAttendanceDrafts | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| DeleteAttendanceDraft | MUTATION | yes | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |

## AuthenticationService (6 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| GetProvisioningState | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: ✔; FT: SecretHygieneAndFailureTests (provisioning/bootstrap paths); BridgeFuzzPayloadTests.InitializeAsync (real GA auth) |
| AuthenticateLocalUser | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: ✔; FT: SecretHygieneAndFailureTests.Argon2*; BridgeFuzzPayloadTests (real session nonce) |
| GetCurrentSession | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| GetNavigationPermissions | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| Logout | MUTATION | yes | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| GetAllowedRoles | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |

## ConfigurationService (12 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| QueryAttendanceStatuses | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CreateAttendanceStatus | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| UpdateAttendanceStatus | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| DeactivateOrDeleteStatus | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| QuerySites | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CreateSite | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| RenameSite | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| RemoveSiteAndReassignCurrentPersons | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| GetCustomFieldDefinitions | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CreateCustomFieldDefinition | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| UpdateCustomFieldMetadata | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| DeactivateCustomField | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |

## DashboardService (4 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| GetDashboardSummary | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryDailyRoster | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryUnprepared | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| GetAutoAttendanceRun | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |

## ExcelSheetsService (11 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| SelectImportWorkbook | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| AnalyzeImport | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewImportPlan | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CommitImport | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| QueryImportOutcome | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| ExportImportIssues | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| GetExportablePeriods | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewMonthlyExport | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| ExportMonthlyWorkbookSet | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| GetExportProgress | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CancelExport | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |

## MonthlyGridService (3 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| GetAvailableArchivePeriods | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryMonthlyGrid | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| GetMonthlyCellDetails | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |

## MovementService (11 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| QueryActivePersonsBySite | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewInternalTransfer | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CommitInternalTransfer | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| PreviewExternalTransfer | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CommitExternalTransfer | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| QueryMovementArchive | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| GetMovementDetails | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewSeparation | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CommitSeparation | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| QuerySeparationArchive | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| GetSeparationDetails | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |

## PersonRosterService (7 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| SearchPersons | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: ✔; FT: BridgeFuzzPayloadTests.HostileTerm_*; PersonRosterService paging/validation (unit coverage deferred to Windows CI run) |
| GetPersonSummary | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryPersons | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: ✔; FT: BridgeFuzzPayloadTests.HostileTerm_* |
| PreviewPersonIdentityUpdate | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| UpdatePersonIdentity | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| PreviewArchivePersons | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| ArchivePersons | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |

## ReportingService (6 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| QueryPersonAttendanceTimeline | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryPersonAttendanceTotals | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| BuildPersonLifetimeReport | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryPersonOperationsTimeline | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| QueryPersonMovements | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| ExportPersonLifetimeExcel | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |

## SettingsBackupService (13 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| GetSettingsOverview | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CreateVerifiedBackup | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| VerifyBackup | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| InspectBackup | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewRestore | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| RestoreDatabaseAtomically | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| GetBackupSchedule | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| UpdateBackupSchedule | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| RunDatabaseIntegrityCheck | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| PreviewFullReset | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| CommitFullReset | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| SetTheme | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| GetApplicationVersion | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |

## UserAuditService (10 actions)

| Action | Kind | Idempotent | Implementation | Tests marked |
|---|---|---|---|---|
| ListUserAccounts | READ | no | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| SearchActivePersonsForAccount | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: ✔; FT: BridgeFuzzPayloadTests.HostileTerm_* |
| CreateUserAccount | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| ChangeUserPassword | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| DeactivateUserAccount | MUTATION | yes | REFUSAL (phase-pending) | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| QueryAuditLog | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| GetAuditEntryDetails | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |
| UpdateAuditPresentation | MUTATION | yes | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| DeleteAuditPresentation | MUTATION | yes | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles) + DangerousActions_* / GaOnlyActions_* / UserHierarchy_* where applicable; FUZZ: envelope-level only; FT: — |
| QueryAuditPresentationChanges | READ | no | **WIRED** | BridgeRbacMatrixTests.EveryManifestAction_RoleGateMatchesContract (4 roles); FUZZ: envelope-level only; FT: — |

## Cross-cutting test evidence index

| Test file | Covers | Directive step |
|---|---|---|
| tests/AlTamam.Tests/Bridge/BridgeRbacMatrixTests.cs | All 90 actions × 4 caller identities (360 allow/deny cases), manifest-mirror equality, dispatcher registration completeness, forged-role payload, GA-only reset family, OA↔SV hierarchy, unknown action, oversized payload, replay conflict, stale-revision guard, origin/frame guards, malformed envelopes, handler-crash containment | 14.03, §20 |
| tests/AlTamam.Tests/Security/BridgeFuzzPayloadTests.cs | Hostile strings (SQL/traversal/XSS/path), draft-field fuzz, JSON depth limit, virtual-host navigation semantics, foreign-origin frames | 14.02 |
| tests/AlTamam.Tests/Security/SecretHygieneAndFailureTests.cs | Argon2id parameters/verify, key format validation, secrets-in-files scan, DPAPI usage review, SQLCipher failure paths | 14.04 |
| tests/AlTamam.Tests/Performance/SqliteIndexPerformanceTests.cs | Index usage plans, synthetic large personnel/month periods, concurrent writers, output-identity before/after | 14.05 |
| build/manifest-guard.py (CI) | 90 unique actions, role vocabulary, reset-family GA-only, mutating⇒idempotent, manifest↔C# drift, CSP strictness | 14.03/15.01 |

## Honest limitations

1. **Zero tests have executed.** No .NET SDK or Windows runtime exists in this sandbox; every "Test marked" row above states *authored* coverage awaiting its first `dotnet test` run on Windows CI. Execution status = NOT RUN (≠ PASS).
2. **71/90 actions are contract-complete but behaviorally pending.** They register correct ownership/roles/mutating flags and refuse honestly at runtime. Wiring belongs to the later functional phases (attendance commit, movements, Excel I/O, backup/restore, reporting, dashboard, user-account CRUD, config CRUD) per the phase plan — recorded here rather than silently half-implemented.
3. The inventory supplied in directive §20 was cross-checked mechanically against the manifest on 2026-10-23: **no mismatches found** (names, counts per service, ownership).
4. READ/MUTATION and idempotency columns are read directly from the manifest per row — not restated from the checklist prose — so any future manifest change invalidates this file until regenerated.

