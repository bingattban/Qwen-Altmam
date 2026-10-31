# PHASE 02 — Encrypted database, temporal identity and historical evidence

**Scope and owner decision:** Phase 02 only. Preserve the existing
`SQLitePCLRaw.bundle_e_sqlcipher 2.1.10` provider and current SQLCipher
database, WAL, DPAPI key and encrypted-backup formats. No provider switch and
no rewriting previously released V001–V011 migration resources.

## Baseline code mapped to 02.01–02.12

- 02.01, 02.08: `AlTamamDbContext.CreateOpenConnection` requires
  `PRAGMA cipher_version`, uses a 32-byte secret and SQLCipher native
  bundle; `WindowsDatabaseKeyStore` creates a new cryptographically random
  32-byte key protected by Windows per-user DPAPI only on first initialization
  and refuses to replace a lost key if encrypted user data still exists.
- 02.02: `V001_InitialSchema.sql` contains Person (`Persons`),
  `PersonIdentityHistory`, `Sites`, `SiteNameHistory`,
  status catalog (`AttendanceStatusDefinitions`), `PersonAssignments`,
  `AttendanceRecords`, `PersonnelMovements`, `Users`, `AuditEvents`,
  `AuditLogPresentationEdits`, `AppSettings` and `BackupHistory`.
  Existing SQL table names are intentional and must not be renamed in place.
- 02.03–02.04: permanent IDs and active person-number uniqueness are
  protected by V001/V007; V010 enforces ASCII-normalized, 4–70-digit
  personnel numbers. Effective periods are [start, end), with indexes
  over identity/person assignments, site, dates, attendance, movements
  and canonical audit records.
- 02.05: V001 assignment-overlap INSERT/UPDATE triggers and a unique
  `(PersonId, AttendanceLocalDate)` attendance key reject conflicting
  writes even across distinct SQLCipher connections. V004 further verifies
  that the attendance date belongs to the referenced historical assignment.
- 02.06–02.07: V004 forbids mutating identity/site/status historical snapshots,
  movement/audit events and attendance corrections; V005 enforces append-only
  audit presentation revisions. Attendance and movement records persist their
  execution-time labels and source/destination identifiers.
- 02.09: `SchemaMigrationRunner` applies ordered hash-pinned migrations
  with transactional per-step rollback; `AlTamamDbContext.InitializeAsync`
  creates a verified, WAL-consistent encrypted pre-migration rescue snapshot
  on existing databases before the first new migration. Prior committed
  migration steps are not automatically rolled back en bloc; the rescue
  snapshot is retained for explicit recovery.
- 02.10–02.11: existing `AlTamam.Tests` test classes
  `SqlCipherConnectionTests`, `SchemaInvariantTests`,
  `Section02HistoricalIntegrityTests`, `MonthlyHistoryReuseTests`,
  and `ArchivedIdentityReturnIntegrationTests` already test encryption,
  earlier-month evidence, explicit dismissed-number reuse vs actual return,
  archive isolation, ambiguous historic matches, external transfers,
  effective-date admission and unique active identities.

## Newly independent Phase 02 integration proof

`tests/AlTamam.IntegrationTests` is included in `AlTamam.slnx` and
executes on BOTH Windows Core and Windows Release on the **same PR head SHA**.
It creates only synthetic, isolated encrypted databases and tests a live
SQLCipher open/reopen, wrong-key and plaintext refusal, mixed Arabic/Persian
digits and leading zeroes, effective-date identity/site/status snapshots,
dismissed-number independence, concurrent overlapping assignment prevention,
audit + visible-overlay transaction rollback, append-only protection and indexes.
Both workflows enforce nonzero dedicated TRX results before packaging.

These tests do **not** prove physical clean-machine signoff or downstream
UI/workflow completion. Full system-reset and cross-provider SQLCipher
replacement are outside this phase and remain unchanged. Only merge after
both workflows, four artifacts and actual SHA-256 checks succeed, then
recheck the merged `main` SHA.
