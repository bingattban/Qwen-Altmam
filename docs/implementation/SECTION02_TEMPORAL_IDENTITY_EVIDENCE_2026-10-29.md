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
