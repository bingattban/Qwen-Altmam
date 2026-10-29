# Section 03 — accounts, roles and sessions: implementation audit (2026-10-29)

**Owner scope:** Section 03.01–03.17 as received verbatim on 2026-10-29. This is a code/test evidence ledger, **not** an independent offline-Windows production acceptance certificate. Section 02 domain and migrations V001–V010 are preserved.

**Main baseline:** `1583e9fce3e24506e77cb59966dbd39f2be5e7e8` (after Section 02 PR #115). Branch: `fix/section03-founding-identity-permanent-20261029`.

| Requirement | Existing implementation or outstanding verification |
| --- | --- |
| 03.01 | `UserRole` and `RoleAuthorizationPolicy` define GeneralAdministration / OrdinaryAdministration / Supervisor; UI uses Arabic role labels. |
| 03.02–03.03 | `BridgeSharedServices.ProvisionFirstAdministratorAsync` validates owner-chosen credentials (founder >=10 characters, also later-user Latin/digit/symbol policy), optional name, transactionally checks `Users`; V006 prevents a second founder, including after deactivation. No default operational credentials. Existing Section03FoundingAccountTests. |
| 03.04 | Founder's `LinkedPersonId=NULL`; optional encrypted `FoundingFullName`, correct account display and immutable actor snapshots. Other accounts must use `UserAccountManagementService.CreateAsync` with active real roster link. **This branch** denies unlinked Admin/Supervisor via the previously public internal bootstrap route and introduces V011 storage-boundary insert/update guards. |
| 03.05–03.08 | `RoleAuthorizationPolicy`, manifest and `WebView2BridgeDispatcher` enforce per-command permissions in C#. Supervisor is denied user management, closed-day corrections, visible-audit edits and full reset. Role forged in JS payload does not override native principal. |
| 03.09 | `PasswordPolicyValidator` enforces >=8 chars, >=3 Latin letters, >=3 ASCII digits, special symbol, and confirmation on creation; `PasswordSecurity` uses salted Argon2id. Temporary lockout: five incorrect logins, 15 minutes, tested via injected time. |
| 03.10 | Server SQLCipher session expiry is exactly 300 s after native foreground user-input renewal, with synthetic 299/300-second tests and background polling not refreshing; `session-idle.js` implements client-side lock. Independent real Windows sleep/resume/clock-change acceptance remains OPEN. |
| 03.11 | Per-user SQLCipher `AttendanceDrafts` are isolated; no commit on session lock. `AttendanceDraftOwnershipIntegrationTests` and `test-attendance-drafts.js`. |
| 03.12 | Explicit Settings > logout revokes the session and outstanding requests; no immediate account-switch shortcut; `Section03InFlightRevocationTests` and session tests. |
| 03.13 | `AccountLifecycleService.SuspendAccountsForPersonnelExitAsync` disables roster-linked accounts and revokes sessions in same transaction as archive, external movement or dismissal; reactivation requires separate authorized audited operation. |
| 03.14–03.15 | `UserAccountManagementService.ListAsync` exposes paginated index, A number, roster full name, role and frozen creator snapshot. Password editing forbids changing username, role or person link; disable/reactivate audited. |
| 03.16 | V009 already detaches linked legacy founders and protects founder links; **V011 additionally forbids the founder's role demotion** that could have bypassed V009 before relinking. Existing V009 may already have detached earlier linked founders without recording their old `LinkedPersonId` in the audit chain; a new migration cannot truthfully reconstruct evidence already lost. Real pre-V009 legacy reconciliation/audit provenance remains a separate acceptance item. |
| 03.17 | Native principal/role gates and permanent `PersonId` scoping preserve identity boundaries; Section02 reuse and Section03 RBAC tests cover hostile payloads. **This branch** additionally checks user-management authorization before querying account existence or roster eligibility and tests that a Supervisor gets identical denials for known and unknown users. Independent end-to-end cross-person historical data exposure tests remain relevant. |

## New regression tests and migration

1. `Section03FoundingAccountTests.InternalBootstrapCannotCreateAnUnlinkedSupervisorOrConsumeFirstRun`: fail-closed and original first-use remains possible.
2. `Section03FoundingAccountTests.FoundingRoleCanNeverBeDemotedToBypassPermanentRosterIndependence`: direct SQL role change and role+link change must be rejected.
3. `Section03FoundingAccountTests.NewNonFounderAccountsMustBeLinkedToAnActiveRosterPersonAtSqlCipherBoundary`: direct orphan insert rejected.
4. V011 `V011_PermanentFoundingRoleAndAccountRoster.sql`: NEW forward-only SHA-pinned embedded migration, no rewriting V001–V010, no direct production data mutation. Active account reactivation verifies roster eligibility. Existing inactive legacy rows are not silently changed.
5. `BridgeSharedServices.CreateUserInternalAsync` is now solely a strong-credential, nonrepeatable founder bootstrap; other roles must use roster-linked role-gated account management.
6. `UserAccountManagementIntegrationTests.SupervisorRejectedAccountActions_NeverRevealAccountExistenceOrRosterEligibility` proves denied password and reactivation operations cannot reveal whether a target exists, belongs to a historical person, or is eligible. C# service checks role authorization before target-specific data reads.

## Merge and release gate

- Tests-first Windows Core and Windows Release run in parallel on exact PR head SHA; both must pass all C# and JavaScript regressions.
- Windows Release must create actual x86/x64 Portable ZIP and x86/x64 Installer EXE, plus SHA-256 evidence, **after passing tests**.
- Merge only after both gates succeed, then independently verify both on exact merged `main` SHA.
- Disconnected clean Windows acceptance for Sections 02+03, real sleep/resume, clock change, power interruption, historical XLSX isolation and owner sign-off are separately pending. Do not label synthetic CI a physical UAT pass.
