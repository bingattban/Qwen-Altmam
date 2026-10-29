## Owner's explicit decision — mandatory operationId at the WebView2 mutation boundary

**Decision dated 2026-10-29:** "نفذ القرار الاول بجعله الزام" — all 37 canonical business/state write actions must provide a well-formed caller-owned `payload.operationId`. Legacy/third-party JavaScript clients omitting the field are deliberately **REJECTED** with `ValidationError` before any handler or SQL transaction executes; no compatibility exemption for their data-writing requests remains at the native bridge entry point.

**Enforcement:** The C# `WebView2BridgeDispatcher` evaluates the *server-defined* mutating metadata after authentication and role verification, ahead of its per-session `requestId` replay gate. The accepted operation ID is **16–128 ASCII letters/digits/underscore/hyphen**, and does not come from a forged role, request ID or JavaScript-only visibility flag. It is required for every data mutation including personnel identity/lifecycle, site/status changes, manual/automatic settings, daily attendance/corrections, transfers, import, user account changes, audit overlays, backup data writes, application reset and temporary draft changes.

**Precisely classified exceptions:** `Logout`, `ExportPersonLifetimeExcel`, `ExportImportIssues`, `ExportMonthlyWorkbookSet`, and `CancelExport` are marked `isMutating` in the existing canonical action catalog, but do not mutate the personnel/business database; these retain independent request-ID replay protection and do not require a fabricated business `operationId`. Read-only preview and query actions are also excluded. The interactive initial founding administrator provisioning remains a separate unauthenticated authentication/setup lifecycle with independent one-use setup and account safety checks; it does not permit unauthorized access to an authenticated business mutation.

**UI compatibility:** The offline `bridge-client.js` sends a unique cryptographically generated `operationId` for each new write payload and retains it **on the same payload object** for a lost-response retry. Existing UI screens that already specify stable IDs retain their own IDs. Requests with explicit malformed IDs are *not* silently repaired; C# rejects them. The CI `manifest-guard.py` verifies that the full C# manifest and the 37-action JS token set never drift, and a JavaScript execution test verifies that the same intent retains its identifier across transmissions. C# security tests exercise every one of the 37 actions through a real dispatcher, confirm malformed/missing IDs do not reach a handler, and retain separate replay and RowVersion tests.

**Idempotence distinction:** A mandatory ID alone does not manufacture ACID receipt support in a service. Domain services previously hardened with `MutationReceipts` and one-use preview/version checks still enforce their own atomic/idempotent behavior. For services with only a request-level anti-replay guarantee, a lost-response retry must follow the service's existing recovery/preview or confirmation procedure; do not claim all commands have identical durable receipt replay semantics.

**Scope:** No V001–V010 edits, no history rewrite, no personnel-number-to-PersonId merge, no changed role policy. This is an intentional compatibility BREAK for legacy clients that omitted IDs; if a custom integration exists it must add stable IDs before sending *new* writes. The release still needs green independent PR Core and Release suites on exact SHA, four x86/x64 packages and SHA-256, merged-main checks, and independent offline Windows acceptance. A CI PASS is not the final independent production certificate.

---

## Finalization follow-up — October 29, 2026: mutation and clock hardening

**Audited baseline:** `efd1af163d4da88a20f0c4861ed2d2903bd8d8b2`. This supplement is a code-and-test implementation record, **not a final production release certificate**.

The full canonical action manifest contains **101 actions, 42 marked mutating**, including non-domain writes such as file exports, draft saving, theme settings, backups, session logout and the intentionally DISABLED destructive restore. Section 02.14 is assessed at the **domain evidence mutation** boundary: person identity/lifecycle, status and site history, attendance, transfers, imports and audit, not a false claim that an Excel file export has a personnel RowVersion.

**Pre-existing verified safeguards:** SQLCipher's forward-only V001–V010 constraints/foreign keys, temporal half-open history, permanent PersonId, mandatory unique active numbers, distinct historical identity/site/status snapshots, append-only audit, RowVersion checks in lifecycle/configuration operations, committed attendance uniqueness, actor-bound one-use previews, source/destination transfer provenance, and 08:00 ledger/20:00 manual lock. Already present `MonthlyHistoryReuseTests`, `MonthlyExcelExportTests`, and `Section02EvidenceAndReportsTests` verify historical attribution across identical institutional numbers, including genuine historical Excel exports. `AutoAttendanceService.RunPendingAsync` already rejects timestamp rollback before processing the daily ledger.

**New test-first hardening in this review:**

- `AdminConfigurationService.RenameSiteAsync`, `UpdateStatusAsync`, and `DeactivateStatusAsync` now replay matching `MutationReceipts` inside the SAME serialized SQLCipher write transaction *before* stale RowVersion checks. Historic label intervals, soft deletion, and audit events are written once. Mismatched reuse of an OperationId fails as a conflict.
- `PersonRosterService.UpdatePersonIdentityAsync` now resolves an existing receipt even when the successful first write has already advanced the individual's RowVersion, then re-verifies/records the receipt atomically during the actual mutation. Both the original PersonId and closed earlier identity interval remain untouched.
- `PersonRosterService.ArchivePersonsAsync` and `RestoreArchivedPersonAsync` now record one durable result per optional caller OperationId in the same transaction as the memberships, active-state changes, and permanent audit. The old one-use preview and strict expected member versions remain in force; different humans never inherit the old PersonId.
- `ExcelPersonnelImportService.CommitAsync` now returns its previous batch result for an identical lost-response retry instead of trying to create duplicate PersonIds. `ManualCarryForwardService.CommitAsync` has the same write-atomic receipt before lock/preview revalidation. In both services legacy token-only calls retain their original single-use behavior and cannot apply twice.
- In-app site/status/person editing and imported Excel confirmation now send distinct operation IDs; an Excel retry confirmation **retains the same operation ID** within its UI closure.
- Added encrypted integration tests for site rename/status change/status deactivation, identity update, archive/re-activation, Excel import, manual batch carry, plus a synthetic reversed-Windows-clock fail-closed test. No pre-existing release migration is edited, no production rows are touched, no historical facts are rewritten.

**Unresolved acceptance distinctions:**

1. **Compatibility boundary:** old/third-party callers that omit `operationId` keep single-use preview tokens, optimistic version conflicts or existing validation; a blanket mandatory request-field switch would break historic clients and many unchanged tests. Fully enforcing mandatory caller OperationId on *every* domain action remains a separate compatibility decision and is not falsely claimed here. The presence of a preview-token-based one-use key prevents double application but does not necessarily return success on a lost-response retry unless a receipt was supplied.
2. **Timing and hardware:** a new deterministic rollback test verifies zero ledger/attendance/audit writes. Independent physical Windows power-loss, timezone/DST transitions, delayed-start testing and customer-machine performance still require acceptance evidence. The older 20:00 scheduler ledger is preserved as historical evidence only.
3. **End-to-end external output and release:** SQLCipher integration tests and synthetic real `.xlsx` comparison cover old-month reuse, but the independent disconnected Windows-machine Excel/UI acceptance matrix and authorizing signoff remain outstanding.
4. **Full reset / restore:** a whole-application reset and the disabled destructive database restore are NOT treated as ordinary reversible mutations. Their own separate authorization/recovery gates remain intact.

**Gating for merge:** exact-PR-head Windows Core and Release MUST both pass all tests; Release tests must finish before x86/x64 Portable ZIP and Installer EXE packaging; four real artifacts and SHA-256 evidence must exist. Then merge and separately verify the resulting `main` SHA. No green CI is an independent production signoff.

---

## Follow-up implementation — Section 02.01 / 02.02 / 02.14 (2026-10-29)

**Baseline reviewed:** `main` at `9c25e7dab5881cd6bea37b2e1cad84984ed89355`; earlier Section 02 evidence remains below, unmodified.

Two verified nonconflicting omissions were addressed in this review branch:

1. **Identity edits, not just identity creation, must canonicalize.** The existing `CreatePersonAsync` validates and normalizes Arabic-Indic/Persian digits, but `PreviewPersonIdentityUpdateAsync` and `UpdatePersonIdentityAsync` previously forwarded the unnormalized entered string to `Persons.CurrentServiceNumberA` and `PersonIdentityHistory.ServiceNumberASnapshot`. Since schema V010 rightly rejects non-ASCII stored numbers, editing the identifier in Arabic-Indic/Persian glyphs could fail only after the confirmation. Both edit paths now validate and normalize using the same `ServiceNumberPolicy` *before* reading/writing any snapshots. Previous identity intervals, immutable PersonId and leading zeros are preserved.
2. **Attendance's lost-response retry now has an ACID result receipt.** `AttendanceCommitService.CommitAsync` still requires a one-time actor-bound preview and verifies person assignments, day, statuses and revision inside its write transaction. A valid, matching `operationId` now supports returning the previously committed result when a WebView2 reply is lost; the receipt is written atomically with the attendance, corrections and permanent audit events. Reusing that operation ID with different request data returns a conflict without a write. A no-`operationId` legacy call preserves its prior one-time-token behavior rather than silently changing earlier client contracts. The WebUI sends one unique operation ID for each confirmed initial or revised attendance intent.
3. **Regression scope:** two C# person-identity tests (canonical Persian input, preserved old number/PersonId, invalid input fails before SQLite), two C# attendance tests (first commit and revision retries leave one audit/revision only), plus JS UI assertions that both commit actions send bounded, distinct operation IDs.

**Not claimed complete:** Requirement 02.14's universal mandatory-`operationId` coverage across every existing mutation API still needs per-action verification, including older clients and imports; do not globally enable a breaking requirement without inspecting each call. End-to-end offline Windows clean-PC acceptance, exhaustive historical Excel reports, clock rollback and power-loss tests, and production restore approval remain OPEN. These changes are not permission to rewrite V001–V010, backfill historical rows, or collapse two permanent PersonIds sharing an institutional number.

**Merge gate:** Both Windows Core and Windows Release must be green on the exact PR HEAD SHA, with Release full test suite before 4 native x86/x64 Portable/Installer artifacts and SHA-256. Then merge and independently check new `main`. Do not mark this addendum as test-pass evidence until those run URLs and SHA actually exist.

---

# Section 02 — Domain, SQLCipher invariants and temporal identity (2026-10-29)

**Governing requirements:** user-supplied Section 02.01–02.17, applying the latest written owner decisions and the original permanent identity/archival policies. **Audited main SHA:** `69337cd9705c598ca5d64359564830bdc3254868`. This is an implementation ledger, NOT independent production signoff.

| Item | Verified implementation and outstanding acceptance |
| --- | --- |
| 02.01 | Permanent `PersonId`, distinct site/status/user IDs, active-number unique partial SQLite index and C# ASCII-canonical validation 4–70 digits are present. **This change adds V010 SQLCipher insert/update guards** so storage cannot accept short/non-digit numbers or disagreement between two current canonical columns. Leading zeros remain text. Existing report code should continue hiding internal IDs in user-facing exports; inspect each final Windows export in acceptance. |
| 02.02 | Effective-dated `PersonIdentityHistory` with half-open nonoverlapping intervals, close-only prior interval and no physical history deletion; actor/timestamp/reason recorded and tested. **Existing.** |
| 02.03 | Stable sites with protected `SITE-UNSPECIFIED`, site-name history and prospective reassignment on deactivation. **Existing C# / SQL triggers.** |
| 02.04 | Four seeded statuses with the specified Arabic names/symbols/colors, adjustable definitions, historical status-label snapshots, permanent StatusId and soft deactivation. **Existing.** |
| 02.05 | Half-open site-assignment intervals and insert/update anti-overlap SQL triggers, indexed by PersonId/site/date. **Existing, tested.** |
| 02.06 | Unique committed attendance per PersonId/local date; not-prepared is a separate state; revision and recorded source/actor. Storage and application tests exist. **Existing.** |
| 02.07 | Separate immutable person IDs for different humans reusing an eligible dismissed number, same-person verified return retains original ID and records a return movement. The explicit decision/preview and no inherited attendance/audit identity are tested. **Existing.** |
| 02.08 | Distinct 08:00 scheduled auto-preparation, independently configured 20:00 manual lock; actual Windows local date and authorized closed-day corrections; movement rule is separate. **Existing; needs real midnight and clock-change acceptance.** |
| 02.09 | SQLCipher ACID write gate, foreign keys, active uniqueness, one attendance/day, half-open identity/assignment/site/status intervals and append-only historical records. **V010 hardens the previously missing database-level canonical number check.** Existing legacy rows are NEVER silently rewritten. |
| 02.10 | Canonical append-only audit with actor account/username/historical name snapshot; founding GeneralAdmin remains unlinked with optional founding name or system label. **Existing, tested.** |
| 02.11 | Visible audit revisions/hiding via separate presentation overlay, with permanent meta-audit; originals cannot be updated/deleted by SQLite triggers. **Existing, tested.** |
| 02.12 | Archive, external transfer and dismissal end active eligibility only; history and immutable PersonId retained. Actual destructive full-app reset remains role-gated and distinct. **Existing.** |
| 02.13 | Old-month reports resolve effective-date personnel identity, number/site and period membership by PersonId; targeted historical-read regressions exist. **Existing; further end-to-end export parity acceptance remains.** |
| 02.14 | The application has row-version stale-write checks and receipt/idempotency for multiple mutations. **PARTIAL:** some legacy mutation routes still allow omission of an `operationId`; an exhaustive mandatory-coverage audit across every batch/transfer action is not proven. Do not claim full compliance. |
| 02.15 | Internal transfer has source/destination day movement annotations and destination attendance eligibility from the following day; external/dismissed people remain in archived records. **Existing under service-level tests.** |
| 02.16 | 08:00 scheduler and durable local-day run ledger, source provenance, idempotent write receipts; 20:00 auto-attendance defaults superseded. **Existing; real clock rollback/power outage acceptance remains.** |
| 02.17 | Number reuse after eligible dismissal retains two distinct PersonIds, immutable old reports and new person with empty operational history; integration tests group by PersonId. **Existing, tested.** |

## Test-first CI failure and synthetic-fixture repair

- Initial PR Windows Core run [40779761870](https://github.com/bingattban2/Qwen-Altmam/actions/runs/40779761870) **FAILED: 97 failed / 735 passed / 832 total**. Most historical tests seeded nonnumeric personnel numbers like `EX001`, `C-1`, `TST-NUM-0`, or `تج-000001` directly through SQL. The new V010 insert guard correctly blocked those invalid synthetic writes, exposing stale fixtures rather than a production-data defect. A founding-account migration test separately assumed the hard-coded schema version `9`.
- Corrected synthetic-only seed values **and their exact expected report/Excel results** across 15 test classes, with no production changes to number validation, SQLCipher triggers, real identities, case logic or history. Updated the founding migration assertion to derive its version from `CurrentSchemaVersion`.
- The second [Windows Release run 40780947692](https://github.com/bingattban2/Qwen-Altmam/actions/runs/40780947692) **FAILED: 7 failed / 825 passed / 832 total**. Those failures were old *test expectations*, not new database validation errors: one Arabic-Indic monthly filter and one Excel number assertion, two manual carry-forward error-message assertions, and three attendance-preview error-message assertions. The three affected synthetic test files were corrected to expect their now-canonical numbers; the monthly filter still explicitly tests Arabic-Indic input. The failed run is retained and is NOT a pass. A third exact-SHA Windows CI run is required before merging.
- These repairs were split into two sequential groups of test-file edits. The original red run is retained as evidence, never represented as PASS. **The full suite must be re-executed on the final PR head**, then both x86/x64 packages generated after a fully passing Release suite before merge.
- Historical nonconforming production rows, if any, were neither read nor modified by the fixture corrections.

## Exact change scope and test-first validation

1. **Tests first:** extended `tests/AlTamam.Tests/Persistence/Section02HistoricalIntegrityTests.cs` with six invalid direct-write samples, 70-digit zero-prefixed acceptance, mismatched column and history-snapshot rejection, and an encrypted V009→V010 forward-upgrade preserving existing legacy evidence.
2. Forward-only SQLCipher migration `src/AlTamam.Infrastructure/Persistence/Migrations/V010_CanonicalServiceNumberIntegrity.sql`: BEFORE INSERT/UPDATE guards on current number columns and BEFORE INSERT on identity snapshots. **Never modify V001–V009**, which are hash-pinned.
3. Registered embedded V010 migration and bumped `CurrentSchemaVersion` to 10 in `AlTamamDbContext`; embedded the SQL resource in `AlTamam.Infrastructure.csproj`.
4. Updated the two-person synthetic-only SQL invariant fixture to store canonical digits and made the existing encrypted pre-migration rescue file test track the current schema number. No real personnel data, SQLCipher keys or production backups used.

### Compatibility and safety notes

- The app's C# service still accepts Western/Arabic-Indic/Persian digit *input* and canonicalizes to ASCII before writing. SQLite stores TEXT so `0001` and 70-digit IDs preserve leading zeros.
- An already-existing pre-V010 legacy row with a nonconforming number is **preserved as-is** in history rather than silently altered; all **new** inserts and number changes are validated. Actual legacy records have not been inspected (no real user data). If an old production database contains such a record, handling its current eligibility/number requires an explicit owner-approved reconciliation, never invented identity or historical deletion.
- Schema upgrades already require a verified encrypted pre-migration rescue. Hash pins and `PRAGMA integrity_check` are enforced in the existing migration runner.
- 02.14 **remains only partially proven** because the helper still supports legacy calls lacking `operationId`. This is identified as a technical compliance gap, **not** an authorized relaxation of the owner's mandate. Existing user workflow must not be broken by blanket token requirements before each calling route is verified.
- No automatic merge: PR SHA Windows Core + Windows Release tests and **all four** Portable/Installer packages with SHA-256 metadata must pass before merging, followed by separate merged-main checks.
- The manual independent offline clean-Windows acceptance, performance hardware thresholds, full restore enablement and final signoff remain open.

**Owner business-policy contradiction:** None newly resolved or silently assumed by this patch. Historical nonconforming identities, if discovered on a real deployment, require owner review before a correction; the migration itself never destroys or merges records.
