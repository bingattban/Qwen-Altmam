# AlTamam — Current C# Bridge Action Coverage

Generated from the current shipped `action-manifest.json` and explicit `BridgeHandlerCatalog.cs` switch arms.
This table is a **routing inventory**, not an assertion that every command passes feature,
Windows E2E, clean-machine or physical restore acceptance tests.

**Manifest:** 103 actions / 11 owning services.
**Explicit native handlers:** 102.
**Intentionally unavailable:** `RestoreDatabaseAtomically` (native physical restore acceptance gate).

## Service-level coverage

| Owning service | Manifest actions | Explicit C# handlers |
|---|---:|---:|
| AttendanceService | 12 | 12 |
| AuthenticationService | 6 | 6 |
| ConfigurationService | 12 | 12 |
| DashboardService | 4 | 4 |
| ExcelSheetsService | 11 | 11 |
| MonthlyGridService | 3 | 3 |
| MovementService | 11 | 11 |
| PersonRosterService | 10 | 10 |
| ReportingService | 6 | 6 |
| SettingsBackupService | 17 | 16 |
| UserAuditService | 11 | 11 |

## Action-by-action routing inventory

Statuses: **WIRED** = explicit native handler (behavior must be tested separately);
**DISABLED** = no allowed execution or default-success fallback.
Idempotency and role data remain authoritative in the JSON manifest and C# mirror.

### AttendanceService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `CommitAdministrativeAttendanceCatchUp` | MUTATION | yes | WIRED |
| `CommitClosedDayCorrection` | MUTATION | yes | WIRED |
| `DeleteAttendanceDraft` | MUTATION | yes | WIRED |
| `GetActiveAttendanceStatuses` | READ | no | WIRED |
| `ListAttendanceDrafts` | READ | no | WIRED |
| `PreviewAdministrativeAttendanceCatchUp` | READ | no | WIRED |
| `PreviewAttendanceChange` | READ | no | WIRED |
| `PreviewClosedDayCorrection` | READ | no | WIRED |
| `ReviseTodayAttendance` | MUTATION | yes | WIRED |
| `SaveAttendanceDraft` | MUTATION | yes | WIRED |
| `SetClosedDayCorrectionPolicy` | MUTATION | yes | WIRED |
| `SubmitAttendance` | MUTATION | yes | WIRED |

### AuthenticationService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `AuthenticateLocalUser` | READ | no | WIRED |
| `GetAllowedRoles` | READ | no | WIRED |
| `GetCurrentSession` | READ | no | WIRED |
| `GetNavigationPermissions` | READ | no | WIRED |
| `GetProvisioningState` | READ | no | WIRED |
| `Logout` | MUTATION | yes | WIRED |

### ConfigurationService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `CreateAttendanceStatus` | MUTATION | yes | WIRED |
| `CreateCustomFieldDefinition` | MUTATION | yes | WIRED |
| `CreateSite` | MUTATION | yes | WIRED |
| `DeactivateCustomField` | MUTATION | yes | WIRED |
| `DeactivateOrDeleteStatus` | MUTATION | yes | WIRED |
| `GetCustomFieldDefinitions` | READ | no | WIRED |
| `QueryAttendanceStatuses` | READ | no | WIRED |
| `QuerySites` | READ | no | WIRED |
| `RemoveSiteAndReassignCurrentPersons` | MUTATION | yes | WIRED |
| `RenameSite` | MUTATION | yes | WIRED |
| `UpdateAttendanceStatus` | MUTATION | yes | WIRED |
| `UpdateCustomFieldMetadata` | MUTATION | yes | WIRED |

### DashboardService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `GetAutoAttendanceRun` | READ | no | WIRED |
| `GetDashboardSummary` | READ | no | WIRED |
| `QueryDailyRoster` | READ | no | WIRED |
| `QueryUnprepared` | READ | no | WIRED |

### ExcelSheetsService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `AnalyzeImport` | READ | no | WIRED |
| `CancelExport` | MUTATION | yes | WIRED |
| `CommitImport` | MUTATION | yes | WIRED |
| `ExportImportIssues` | MUTATION | yes | WIRED |
| `ExportMonthlyWorkbookSet` | MUTATION | yes | WIRED |
| `GetExportProgress` | READ | no | WIRED |
| `GetExportablePeriods` | READ | no | WIRED |
| `PreviewImportPlan` | READ | no | WIRED |
| `PreviewMonthlyExport` | READ | no | WIRED |
| `QueryImportOutcome` | READ | no | WIRED |
| `SelectImportWorkbook` | READ | no | WIRED |

### MonthlyGridService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `GetAvailableArchivePeriods` | READ | no | WIRED |
| `GetMonthlyCellDetails` | READ | no | WIRED |
| `QueryMonthlyGrid` | READ | no | WIRED |

### MovementService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `CommitExternalTransfer` | MUTATION | yes | WIRED |
| `CommitInternalTransfer` | MUTATION | yes | WIRED |
| `CommitSeparation` | MUTATION | yes | WIRED |
| `GetMovementDetails` | READ | no | WIRED |
| `GetSeparationDetails` | READ | no | WIRED |
| `PreviewExternalTransfer` | READ | no | WIRED |
| `PreviewInternalTransfer` | READ | no | WIRED |
| `PreviewSeparation` | READ | no | WIRED |
| `QueryActivePersonsBySite` | READ | no | WIRED |
| `QueryMovementArchive` | READ | no | WIRED |
| `QuerySeparationArchive` | READ | no | WIRED |

### PersonRosterService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `ArchivePersons` | MUTATION | yes | WIRED |
| `CommitArchivedReturn` | MUTATION | yes | WIRED |
| `CreatePerson` | MUTATION | yes | WIRED |
| `GetPersonSummary` | READ | no | WIRED |
| `PreviewArchivePersons` | READ | no | WIRED |
| `PreviewArchivedIdentity` | READ | no | WIRED |
| `PreviewPersonIdentityUpdate` | READ | no | WIRED |
| `QueryPersons` | READ | no | WIRED |
| `SearchPersons` | READ | no | WIRED |
| `UpdatePersonIdentity` | MUTATION | yes | WIRED |

### ReportingService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `BuildPersonLifetimeReport` | READ | no | WIRED |
| `ExportPersonLifetimeExcel` | MUTATION | yes | WIRED |
| `QueryPersonAttendanceTimeline` | READ | no | WIRED |
| `QueryPersonAttendanceTotals` | READ | no | WIRED |
| `QueryPersonMovements` | READ | no | WIRED |
| `QueryPersonOperationsTimeline` | READ | no | WIRED |

### SettingsBackupService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `CommitFullReset` | MUTATION | yes | WIRED |
| `CreateVerifiedBackup` | MUTATION | yes | WIRED |
| `GetApplicationVersion` | READ | no | WIRED |
| `GetAutoAttendancePolicy` | READ | no | WIRED |
| `GetBackupSchedule` | READ | no | WIRED |
| `GetSettingsOverview` | READ | no | WIRED |
| `InspectBackup` | READ | no | WIRED |
| `PreviewFullReset` | READ | no | WIRED |
| `PreviewRestore` | READ | no | WIRED |
| `RestoreDatabaseAtomically` | MUTATION | yes | DISABLED |
| `RunDatabaseIntegrityCheck` | READ | no | WIRED |
| `SetAttendanceSchedule` | MUTATION | yes | WIRED |
| `SetAutoAttendanceDefaultStatus` | MUTATION | yes | WIRED |
| `SetOutputDirectory` | MUTATION | yes | WIRED |
| `SetTheme` | MUTATION | yes | WIRED |
| `UpdateBackupSchedule` | MUTATION | yes | WIRED |
| `VerifyBackup` | READ | no | WIRED |

### UserAuditService

| Canonical action | Kind | Declared idempotency | Native routing |
|---|---|---|---|
| `ChangeUserPassword` | MUTATION | yes | WIRED |
| `CreateUserAccount` | MUTATION | yes | WIRED |
| `DeactivateUserAccount` | MUTATION | yes | WIRED |
| `DeleteAuditPresentation` | MUTATION | yes | WIRED |
| `GetAuditEntryDetails` | READ | no | WIRED |
| `ListUserAccounts` | READ | no | WIRED |
| `QueryAuditLog` | READ | no | WIRED |
| `QueryAuditPresentationChanges` | READ | no | WIRED |
| `ReactivateUserAccount` | MUTATION | yes | WIRED |
| `SearchActivePersonsForAccount` | READ | no | WIRED |
| `UpdateAuditPresentation` | MUTATION | yes | WIRED |

## Unresolved acceptance evidence (not marked PASS)

- The 69-route screen/navigation inventory is not proof of each route's full end-to-end contract.
- Error metadata, actor identity and durable audit receipts require real service/bridge tests per action.
- Missing, skipped, running and failed automatic attendance attempts are not durably recorded
  by the completion-only ledger; an unrecorded due time is **not** a proven failure.
- Destructive `RestoreDatabaseAtomically` stays disabled until owner-approved native
  restore, rollback and independent physical Windows acceptance pass.
- Four packaged Windows artifacts and SHA-256 passing CI do not replace owner clean-machine
  offline acceptance or production signoff.

Regenerate with `python build/generate-manifest-action-coverage.py` and verify
with `python build/generate-manifest-action-coverage.py --check`.
