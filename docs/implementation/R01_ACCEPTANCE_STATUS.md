# R01 acceptance status — 2026-11-07

**R01 is OPEN.** Successful incremental PRs are not full phase acceptance.
Current verified baseline: merged #253, `fb3cbd898af36869e4a58e8ad273faf984d1e520`.
The #253 bounded-budget repair passed both Windows workflows and all four packages before merge (see verified follow-up below).
Do not start a different corrective package or label this application production-ready.

## Verified Windows baseline

PR #250 exact head `b13702177cecc06c31da7ccb93b7e4d5d506a4f0`:

- Core 42371736847: success.
- Release 42371736832: success; tests completed before packaging.
- x86/x64 Portable ZIP and Installer EXE: present, nonexpired, same source head.
- Independent four-file SHA256 provenance audit: success.
- No blocking reviews or unresolved review threads at merge.
- Independent manual clean-machine acceptance: owner-deferred, NOT EXECUTED.

Historical incremental evidence is retained in previous versions of this ledger and PRs.
Results for a recorded head do not prove unexecuted changes on a later head.

## Requirement ledger

| Requirement | Established evidence | Closure still required |
|---|---|---|
| 4.1 six historical actions | Explicit runtime registrations; aliases retain canonical native report behavior; restore explicitly refuses | Do not infer full phase closure from routing |
| 4.2 statuses | Real SQLCipher ordering, active/deactivated/empty data, expired session; serialized fields; actual attendance initialization, leave controls, preview cancel, durable confirmation/revision; actual report filters | Remaining dashboard/correction consumers and isolated failed-lookup behavior |
| 4.3 search | Native normalized digits/scope/limits/order/role boundaries; real WebView2 bridge and actual report search/group selection with stable identity | Full account-picker/navigation consumer boundary and per-role failure cases |
| 4.4 historical reports | Three retained aliases equal canonical filtered native DTO for all roles; synthetic group Excel aggregate/row-count parity; actual one-person group report consumer after revision | Full filtered/paged/single/group/historical report-to-Excel parity matrix |
| 4.5 restore | Real-login live/expired refusal, no writes, backup hash/integrity; explicit disabled handler | Preserve disabled state; destructive restore belongs to separate R08 authorization |
| 4.6 accountability | Missing handler factory fails; explicit disabled refusal; RED/GREEN evidence in #239 | Registration alone is not 4.7 business acceptance |
| 4.7 runtime gate | Real native mutation persistence/replay/conflict/stale/denied/expired negatives; native status/search/read slices; full mocked RBAC matrix separately identified; merged 38-action native lifecycle/snapshot slice passed Windows; personnel extension also passed Windows; current operational extension pending Windows | A meaningful real authenticated SQLCipher scenario for EVERY enabled UI-used action, integrated with duplicate/role/mutation/request-response disposition checks |
| 4.8 schemas | Real native read/mutation/error serialization; current schema repair below adds automated envelope and four status/search data projections, strict vocabulary and negative controls; merged 38-action response-shape slice passed Windows; personnel extension also passed Windows; current operational extension pending Windows | Data schemas/snapshots and native consumer parity across ALL required actions and roles |
| 4.9 closure | Explicit enabled/disabled routing inventory; honest residual ledger | Cannot close until all required cells above have executed passing evidence |

## Current consolidated schema repair

See `R01_RESPONSE_SCHEMA_REPAIR.md` for the source-level defect, test-first SHA,
commands, observed RED and intermediate GREEN. This is a verified schema discrepancy:
the old response schema forbids fields actually emitted by C# (`code`, `messageAr`,
audit/conflict/version metadata). Correct only the schema, preserving wire compatibility.
The native tests check real login/dispatcher/SQLCipher responses against the declared
vocabulary and status/search data projections; unsupported schema keywords fail closed.

Local observed baseline RED: 3 failed, 0 passed, 0 skipped at test-only commit
`9cbdcdfc95e61f93e2b6ed52685f31cd280d9243`. The same three tests passed after correction.
Final expanded R01/envelope run: 22 passed, 0 failed, 0 skipped (native SQLCipher).
The response projection lookup was then made fail-closed on a missing schema definition;
the affected five-test group then passed again: 5 passed, 0 failed, 0 skipped.
Windows verification of the new final head is still required before merge.

## Completion and safety rule

Do not call a missing, pending, skipped or failed requirement verified. The complete
104-action inventory and green CI are not substitutes for meaningful executed outcomes.
Preserve owner roles, strict origin/nonce/replay, independent schedules, immutable history,
SQLCipher, disabled restore and every required test. No new policy conflict was identified
in the schema correction. No performance optimization or speedup is claimed.

Normal merge requires final-head Core and Release success, tests before packaging,
four genuine x86/x64 outputs, independent SHA256 audit and no blocking review.
Production publishing and physical Windows acceptance remain separate owner gates.

## Native configuration lifecycle execution gate (merged #251)

Base: `2146cba238c10cd2a288be48e5e2978182a1cabe`. Classification: acceptance
coverage gap, not a new production defect. No production code, schema migration,
permission, policy or performance optimization is changed by this slice.

`R01NativeConfigurationLifecycleTests` uses five independent unpooled SQLCipher
fixtures, real first-use provisioning/authentication through the dispatcher and
real nonce resolution. Its 38 successful actions are the exact keys in
`tests/AlTamam.Tests/Bridge/Snapshots/R01NativeConfigurationResponseShapes.json`.
Each success is checked against the response envelope schema and its committed
field/type snapshot; snapshots contain no credential, nonce, person or path values.

| Native scenario | Independently checked durable outcomes |
|---|---|
| Sites and statuses | Created IDs, renamed effective-date histories, site retirement, deactivated status, preserved histories, exact replay receipts and no duplicate audit |
| Custom columns | Stable column/technical IDs, metadata edit, hidden/inactive state, unchanged stored value, retained audit, replay safety |
| Independent settings | Output directory and theme; independently persisted attendance/default/lock/backup/monthly-export settings; native readback, integrity and replay |
| User accounts | Stable person linkage, password rotation, deactivate/reactivate row versions, one audit per mutation, real login using changed password; nine Supervisor-denied native writes with no data/audit/invalidation or durable changes |
| Audit display | Real query/edit/hide/details/history, original event/hash/description retained, valid chain, two overlays, idempotent replay |

`build/check-r01-native-contract-evidence.py` consumes the actual full-suite TRX.
It requires all five named cases to have passed and every committed action shape
to appear in native test output; it rejects missing/failed/duplicate cases,
missing actions and mismatched shapes. Six validator negative/control tests pass.
The new step runs after full native tests in BOTH Windows workflows, before
packaging. Existing required job names and tests-before-packaging dependencies
remain unchanged. There is no snapshot-update mode or bypass in CI.

Observed local capture run: five native cases passed, no failures/skips; the
actual TRX contains 38 actions/38 wire shapes and passes the evidence validator.
The final rerun with strict committed snapshot comparison and the broader R01
suite is recorded separately below. Windows execution on the new final head is
still required. Do not add this subset count to older inventories or call it
complete coverage of all 104 canonical actions.

Final local verification after enabling strict committed snapshot comparison:
27 R01/envelope cases passed, 0 failed, 0 skipped; TRX `r01-lifecycle-final.trx`.
The actual final TRX passed the execution gate: 5 native lifecycle cases / 38
successful actions with matching snapshots. Six evidence-validator tests, five
Phase00-02 guard regressions, six Section08 checks, eleven WebView2 preflights,
the 104-action request-schema guard and both workflow YAML parses passed.
.NET SDK 10.0.403; Windows WPF and packaged native checks are not executed locally.


## Native personnel identity and archive extension (merged #252)

Base: merged #251. The configuration slice above passed Windows Core and
Windows Release, including all four package artifacts and independent SHA256
audit, before merge. This extension is an acceptance coverage gap, not a
production defect; no production behavior, role or packaging dependency changes.

Two additional isolated authenticated SQLCipher scenarios execute through the
real dispatcher. Identity preview does not alter the person or audit; commit
canonicalizes Persian digits with leading zeros, retains the original effective
dated identity and stable PersonId, and writes exactly one audit. Replay returns
the original receipt; an independently retried stale revision fails unchanged.
Archive preview is read-only; confirmation closes the original assignment,
removes the person from default QueryPersons, retains the archived query and
GetPersonSummary identity, and writes one archive audit. Exact replay is stable;
changed intent under the same operationId and a fresh operation with the old
preview both fail without another audit. Both databases pass integrity checks.

The initial two-case capture passed locally with no failures or skips. Snapshots
were captured only from those successful serialized native replies, then strict
snapshot comparison was restored. The evidence gate now requires seven named
cases and all 44 action keys; QueryPersons and GetPersonSummary include distinct
empty/history-populated response shapes. There is no CI snapshot update bypass.
Final strict rerun and Windows status are recorded separately when available.
R01 remains OPEN: full enabled-action, consumer, role/error-schema and Excel
parity coverage still requires completion; deferred manual Windows acceptance
has not been executed.

Final strict local verification: 29 R01/envelope cases passed, 0 failed, 0 skipped
with .NET SDK 10.0.403 (TRX `r01-personnel-final/final.trx`). The real TRX passed
the execution gate: seven native lifecycle cases / 44 successful action keys.
Six evidence-validator checks, six Section08 contract checks, five Phase00-02
guard checks, the 104-action typed request-schema guard and diff whitespace
checks passed. Windows Core/Release and four-package verification remain
required on the new PR head before merge. No local Windows/package execution
is claimed.


## Native attendance, movement and separation extension (current review branch)

Base: merged #252. Windows Core `42387825790` and Windows Release `42387825788`
both passed 1069 main-suite tests on head
`b0e304acc2eecfe15918652df1e614b8f86898ac`, plus their independent SQLCipher and
unit suites. The seven-case / 44-action evidence gate passed on both runners.
Both x86/x64 Portable and Installer jobs and the independent four-package SHA256
audit passed before merge. Manual clean-machine acceptance remains deferred and
NOT EXECUTED, a separate production GO decision.

This new slice is acceptance coverage, not a verified new production defect.
No production service, role, schema migration, schedule or packaging dependency
is changed. Three new isolated unpooled SQLCipher fixtures provision/login via
the real dispatcher and resolve real session nonces. The business clock is
injected at noon so attendance eligibility does not depend on CI wall-clock hour.

| Native scenario | Independent business assertions |
|---|---|
| Attendance | Save/list/delete owned draft without attendance; missing leave dates rejected; preview read-only; Present commit then Absent revision; exact receipt replay; consumed preview rejected for new operation; exactly two audits, one attendance and one revision; daily/unprepared/dashboard/monthly/detail and canonical report/aliases agree |
| Transfers | Internal and external preview/commit/replay; source history retained and membership changes immediately; transferred-day attendance explicitly forbidden, next-day destination preview allowed after real reauthentication; archived source/destination/filter/detail facts, external inactive state, correct month markers and report movement count; no fabricated attendance |
| Separation | Real server five-second delay refuses early commit; after clock advance, one movement, inactive/separated person and closed original assignment; replay without duplicate audit; reason and stable PersonId retained in archive/details; historical monthly separation marker, no invented attendance |

The attendance scenario also verifies real navigation permission, offline/protocol
metadata, active-status DTO and Arabic-digit person/account searches. An empty
auto-attendance ledger read must not fabricate a receipt or create a run.
Monthly `days` response snapshots validate ISO date keys and normalize only that
known date-keyed map, retaining each distinct cell shape. A corrupted copy of
the actual monthly reply with a non-date key must fail the snapshot descriptor;
this negative control is not a substitute for the real native business tests.
The evidence gate now requires all ten explicitly named native cases across the
two fixture classes; the actual response shape count and final results follow
after successful capture and strict comparison rerun. R01 remains OPEN.


Successful capture evidence: Attendance and Separation cases passed in the
operational capture TRX; the corrected Transfer case passed independently in
`r01-transfer-capture-final/capture.trx`. Only PASSED case stdout was used to
extend snapshots. The slice now contains 78 canonical action keys. Strict
snapshot comparison is enabled for every success in BOTH fixture classes.
The actual monthly response also has a negative control for wrong cell value
type, so date-key normalization cannot hide a DTO shape change. Seven validator
regressions include rejection of configuration-only output when operational
cases were not executed. No production defect/fix claim follows from test
fixture assumptions corrected during construction. Final strict results follow.


Final strict local verification: 32 R01/envelope cases passed, 0 failed, 0 skipped
with .NET SDK 10.0.403, TRX `r01-operations-final/final.trx` (4m48s).
The actual final TRX passed the evidence gate: ten named native lifecycle cases /
78 successful canonical action keys with committed response shapes. Seven
validator regressions, six Section08 contract checks, five Phase00-02 gate
checks, the 104-action request-schema guard and whitespace checks passed.
Windows Core/Release plus all four x86/x64 Portable/Installer artifacts and
independent hashes on the final new PR head are required before merge.
Windows WPF/package execution is NOT claimed locally. Full enabled-action,
per-role/error/consumer and Excel parity requirements still keep R01 OPEN.


## Post-merge x86 package timeout investigation and bounded budget repair

The owner-reported push release `42391468076` on merged #252 commit
`13305e98e79863720703cb3713466367ab123796` passed its full tests and x64 package
job. The x86 Portable actual UI/native/SQLCipher process reached UI-E2E PASS,
then exceeded the 300-second external cap on BOTH attempts; it never reached
route evidence. The four-package audit was correctly skipped. This is a
post-merge release failure, not a Git conflict or proof that tests were bypassed.

Independent prior success on the same source tree in PR release `42387825788`,
x86 job `128451871961`, measured 271.95 seconds for Portable native E2E and
275.79 seconds for Installer native E2E, plus separate routes (19.51/4.69 seconds).
The old 300-second bridge cap had under 9% headroom above that successful
Installer measurement. The slow phase's internal cause is NOT established by
the startup-only failing trace; a reproducible application defect is not claimed.
A retry of only failed jobs on the original merge SHA was requested for diagnosis.

The proposed CI repair adds an executable architecture-specific budget policy:
480 seconds for x86 real bridge E2E, 300 seconds for x64. Both Portable and
Installer use it. Separate routes stay at 120 seconds, simple navigation at
90 seconds, at most two attempts, the 30-minute package job limit, process-tree
termination and all exit/trace/role/SQLCipher/69-route/AppData/SHA256 conditions
remain required. This does not turn a timeout into success or change application
security, password cost, data, roles or services.

Test-first record: the updated packaged WebView contract guard failed before
script changes (1 failed / 10 passed). The actual Windows PowerShell watchdog
regression now also evaluates both policy values and rejects an unsupported
runtime, in addition to its existing real subprocess nonzero/timeout/missing
negative tests. That Windows-native test and actual x86/x64 packaged E2E are
NOT EXECUTED locally (no Windows/PowerShell runtime); both workflows run them
before acceptance. After script changes the 11 WebView preflight checks and
five Phase00-02 regressions passed locally. Windows/package validation on the
new final PR head is still REQUIRED before calling this repair verified or
merging #253. The earlier 32-case native R01 local result predates this PS-only
repair; no C# application or native lifecycle test source was changed by it.


## Verified #253 merge and repository compatibility review

Final head `1b7483ffaab7cdc3fd9a7b5879d45cf53be78794` passed Core
`42395171854` and Release `42395171897`. Each main suite executed 1072 tests
with zero failures/skips; the independent Release SQLCipher suite passed 18
and authorization unit suite passed 14. The live repository guard verified
69 routes, 104 actions, 8 projects, both gated Windows workflows, migrations
V001-V014, pinned SQLCipher and disabled restore. Review found no application
service/role/schema behavior change, missing dependency or merge conflict;
changes were native acceptance evidence plus the bounded CI budget repair.
No unresolved review submissions/threads were present.

Real x86 packaged bridge E2E passed in 345.82s (Portable) and 349.12s (Installer),
above the old 300s cap and within the new 480s cap. Both architectures' actual
Portable/Installer acceptance and the independent four-package SHA256 audit
passed. All four non-expired artifacts and provenance identified the same head.
Merge commit: `fb3cbd898af36869e4a58e8ad273faf984d1e520`.
Independent manual clean-machine acceptance remains deferred and NOT EXECUTED.
This review does not certify untested flows or close R01.

## Native Excel/import parity extension (current review branch)

Classification: missing acceptance coverage; no production fix/optimization.
Three isolated authenticated dispatcher/SQLCipher scenarios use real XLSX bytes
and read every exported cell, including actual number/date styles. Native setup
selects an isolated output directory; all generated files remain temporary.
Fixture history is inserted at its original effective dates; it is not rewritten.

- Monthly: 105 distinct people, two sites, three independently known October
  attendance cells, current versus historical identity; all 31 day columns and
  ordered identity/site fields in consolidated and both site workbooks exactly
  match all native pages (100 plus 5). A name/site-filtered workbook and completed
  session-owned export progress are checked. Cancel after completion is refused;
  active writer cancellation is still an open acceptance case, not success-covered.
- Individual/group reports: 103 known attendance records cross the real fixed
  100-row report boundary (100 plus 3); historical name/000123 versus current
  name/009000 remain distinct. Genuine numeric Excel dates/times are decoded
  from OOXML styles before comparison; all historical columns and status counts
  match native report rows. The filtered two-person group retains two individual
  rows, an explicit combined total and a separate date/status aggregate.
  Individual workbook parity already passed with real GeneralAdministration,
  OrdinaryAdministration and Supervisor logins. The strict final run additionally
  checks every grouped worksheet under those same three real roles.
- Import: real workbook selection, analysis, preview, commit, stable receipt retry,
  owned outcome and genuine issues workbook; one accepted leading-zero identity,
  one rejected row, original duplicate identity untouched, one import audit and
  no executable XLSX formula despite formula-like rejected input text.

Successful three-case capture passed before enabling strict comparison.
Snapshots now contain 89 canonical action keys. Both TRX gates require all
13 explicitly named native cases across configuration/operations/Excel classes;
configuration-only evidence cannot satisfy either added group. Final strict
results follow. Remaining enabled actions, consumer/error/role/schema coverage,
full mode/filter/export parity and independent manual acceptance keep R01 OPEN.


Final strict result: 35 R01/envelope cases passed, 0 failed, 0 skipped
(.NET SDK 10.0.403, `r01-excel-verified-final/final.trx`, 3m16s).
The actual final TRX passed the gate: 13 real native cases / 89 action keys.
This final run includes fully related/content-typed OOXML import input and
all individual AND grouped worksheets under real GA/OA/Supervisor sessions.
Eight evidence-validator tests, six Section08 contract checks, five Phase00-02
regressions, eleven WebView self-check preflights, the 104-action request-schema
and live repository invariant guards and diff whitespace checks passed.
No capture/update bypass remains in the tests. Windows Core/Release and all
four package/hash checks on the new head remain required before merge.

The 14 enabled actions outside this named successful native slice are:
PreviewArchivedIdentity, CommitArchivedReturn, CancelExport (active writer),
CreateVerifiedBackup, VerifyBackup, InspectBackup, PreviewRestore,
PreviewFullReset, CommitFullReset, PreviewAdministrativeAttendanceCatchUp,
CommitAdministrativeAttendanceCatchUp, SetClosedDayCorrectionPolicy,
PreviewClosedDayCorrection, CommitClosedDayCorrection. Disabled
RestoreDatabaseAtomically remains explicitly refused by its separate real
native tests and is not falsely counted as successful implementation.
This inventory is not proof that every branch/role/UI consumer of the 89 named
actions is complete. Per-consumer failure isolation, complete wire/error/null
and role variants, and remaining report/export modes still require acceptance.
