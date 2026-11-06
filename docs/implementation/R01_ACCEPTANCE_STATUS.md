# R01 acceptance status

Evidence baseline: merge f03ecdfa49c1b762dbf7ccc9632003b35c21ec8b (PR #241).
This is an acceptance ledger, not a claim that the entire application is fixed.

| Requirement | Evidence established | Remaining acceptance |
|---|---|---|
| 4.1 historical six actions | Explicit registrations inspected; 103 enabled routing arms and one explicit disabled-restore refusal | Routing alone does not prove all business outcomes |
| 4.2 statuses | #233/#234: native SQLCipher active projections, ordering, deactivation, empty list, expired session; #241 serialized response | Native-connected JS attendance initialization, selectors, review/dashboard/correction/report flows and failure isolation |
| 4.3 search | #235: digit normalization, limits/order/role scope and DOM picker; #241 real-login serialized person fields | Native-connected JS search/pick/navigation, full cross-role accepted/denied boundary cases |
| 4.4 historical reports | #237 native canonical parity for three roles; existing report/Excel fixtures | Cross-action Excel parity extension below is pending Windows; broader single/group filters/paging/history evidence still incomplete |
| 4.5 disabled restore | #238 real-login live/expired refusal, durable counts, backup hash, integrity | Preserve disabled availability; actual restore belongs to separate R08 owner gate |
| 4.6 registration | #239 RED in Core/Release then GREEN: unknown handler factory throws, explicit meaningful disabled restore refusal | Full per-action runtime disposition/schema gate remains under 4.7 |
| 4.7 runtime gate | #240 real-login mutation persistence/replay/conflict, stale request, revoked session, actual Supervisor-denied account write | Machine-enforced matrix covering EVERY enabled UI-invoked action with meaningful native SQLCipher result; duplicate/role/mutation/schema checks integrated with executed coverage; do not substitute static action counts |
| 4.8 wire contracts | #241 actual native serialized status/search/expiry shapes | Automated schema validation and snapshots across all required responses, native-connected JS consumers, mutation receipt/invalidation/per-role parity |
| 4.9 final acceptance | Incremental checks pass on their recorded heads | Cannot close R01 until residual required evidence above passes and final matrix classifies enabled/disabled/owner-decision actions |

## Verified CI baseline

PR #241 head 1b761ca14389707f5400d70cb5b1b07e17bcdca5:
- Core run 42264624801: success.
- Release run 42264624799: success.
- Release tests precede packaging; x86/x64 Portable and Installer artifacts exist on this head.
- Independent four-package SHA256 audit: success.
- Independent clean-machine manual gate: skipped, NOT EXECUTED. CI is not manual acceptance.

Prior results are evidence for the recorded head, not automatic proof of later changes.

## Current incremental extension

PersonnelExcelExportTests.GroupWorkbook_UsesIndependentDisplayOrdinalsWithoutExposingPersonId_ExportAudited:
compare the existing SQLCipher group fixture with four actual dispatcher report actions,
then compare attendance totals and detailed row count with the real OOXML workbook.
The fixture includes archived/new identities sharing a personnel number; existing assertions
retain independent identity ordinals, historical snapshots, formula safety and export audit checks.
Synthetic resolved principal is used here; this is not a real-login WebView2 E2E test.

Local JS report and Section08 preflight checks pass. New native extension: NOT EXECUTED locally
(dependency restore unavailable), awaiting Windows CI. No production changes or defect claim.
Full schema validation, all-action gate and complete historical Excel parity are not implied
by this narrow aggregate/row-count comparison.

## Completion rule

Do not mark a missing, pending, skipped or failed requirement as verified.
Reads require correct results without business mutation; writes require durable outcomes,
audit and replay safety; disabled commands require truthful safe refusal.
Do not activate restore, remove historical aliases or change owner policies to close a checklist.
Merge each final head only after Core, Release, four outputs and provenance pass.
