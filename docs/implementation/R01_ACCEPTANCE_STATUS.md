# R01 acceptance status — 2026-11-07

**R01 is OPEN.** Successful incremental PRs are not full phase acceptance.
Current verified baseline: merged #250, `2146cba238c10cd2a288be48e5e2978182a1cabe`.
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
| 4.7 runtime gate | Real native mutation persistence/replay/conflict/stale/denied/expired negatives; native status/search/read slices; full mocked RBAC matrix separately identified; new 38-action native lifecycle/snapshot slice passes locally, pending Windows | A meaningful real authenticated SQLCipher scenario for EVERY enabled UI-used action, integrated with duplicate/role/mutation/request-response disposition checks |
| 4.8 schemas | Real native read/mutation/error serialization; current schema repair below adds automated envelope and four status/search data projections, strict vocabulary and negative controls; new 38-action response-shape snapshots pass locally, pending Windows | Data schemas/snapshots and native consumer parity across ALL required actions and roles |
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

## Native configuration lifecycle execution gate (current review branch)

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
