# R01 acceptance status — 2026-11-07

**R01 is OPEN.** Successful incremental PRs are not full phase acceptance.
Current verified baseline: merged #249, `667a44effa691390155520bc4d837b9b81c5d3a0`.
Do not start a different corrective package or label this application production-ready.

## Verified Windows baseline

PR #249 exact head `4743a4ffb669efb92630fe7aa47581aa5f9234f9`:

- Core 42368226683: success.
- Release 42368226743: success; tests completed before packaging.
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
| 4.7 runtime gate | Real native mutation persistence/replay/conflict/stale/denied/expired negatives; native status/search/read slices; full mocked RBAC matrix separately identified | A meaningful real authenticated SQLCipher scenario for EVERY enabled UI-used action, integrated with duplicate/role/mutation/request-response disposition checks |
| 4.8 schemas | Real native read/mutation/error serialization; current schema repair below adds automated envelope and four status/search data projections, strict vocabulary and negative controls | Data schemas/snapshots and native consumer parity across ALL required actions and roles |
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
