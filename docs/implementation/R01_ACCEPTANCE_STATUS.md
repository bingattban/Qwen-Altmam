# R01 acceptance status

Evidence baseline: merge f03ecdfa49c1b762dbf7ccc9632003b35c21ec8b (PR #241).
This is an acceptance ledger, not a claim that the entire application is fixed.

| Requirement | Evidence established | Remaining acceptance |
|---|---|---|
| 4.1 historical six actions | Explicit registrations inspected; 103 enabled routing arms and one explicit disabled-restore refusal | Routing alone does not prove all business outcomes |
| 4.2 statuses | #233/#234: native SQLCipher active projections, ordering, deactivation, empty list, expired session; #241 serialized response | Native-connected JS attendance initialization, selectors, review/dashboard/correction/report flows and failure isolation |
| 4.3 search | #235: digit normalization, limits/order/role scope and DOM picker; #241 real-login serialized person fields | Native-connected JS search/pick/navigation, full cross-role accepted/denied boundary cases |
| 4.4 historical reports | #237 native canonical parity for three roles; existing report/Excel fixtures | Cross-action Excel aggregate/row-count parity below passed Windows; broader single/group filters/paging/history evidence still incomplete |
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

Local JS report and Section08 preflight checks pass. Native extension passed Windows Core 42272876295 and Release 42272876264 on
76f458af113c9bffe1f6cbf2a4de8b49520d6424, with four-package provenance verification. No production changes or defect claim.
Full schema validation, all-action gate and complete historical Excel parity are not implied
by this narrow aggregate/row-count comparison.

## Completion rule

Do not mark a missing, pending, skipped or failed requirement as verified.
Reads require correct results without business mutation; writes require durable outcomes,
audit and replay safety; disabled commands require truthful safe refusal.
Do not activate restore, remove historical aliases or change owner policies to close a checklist.
Merge each final head only after Core, Release, four outputs and provenance pass.

## Verified native-connected browser slice (#243)

Phase04BridgeRoundTripSelfCheck now invokes GetActiveAttendanceStatuses, SearchPersons and
SearchActivePersonsForAccount from the shipped JavaScript bridge in real WebView2. It checks
status field shapes and selectable HTML, Arabic/Persian-digit person identity and items/count,
and rendered HTML options. C# rejects a pass result missing either new probe flag.
This is a real JS/native/SQLCipher round trip in the disposable fixture, not full attendance or
admin-settings screen workflow acceptance. Local JavaScript syntax and 11 self-check guards pass;
Windows Core 42278833402 and Release 42278833267 passed on exact head
235e8f8f5467b3d1cb67b257fe1b2c5fa1806073, including native E2E before packaging,
four x86/x64 Portable/Installer outputs and independent SHA256 audit. Merged as
4076dc6c8a139bcece4ce600d9bfcbf40e4697ee. Independent manual clean-machine acceptance
remains NOT EXECUTED. R01 is not complete.

## Browser session recovery slice (pending Windows)

Base: 4076dc6c8a139bcece4ce600d9bfcbf40e4697ee. Extend only the existing disposable
Phase04BridgeRoundTripSelfCheck; no production behavior or optimization changes.
Acceptance: revoke the real Supervisor session; each of GetActiveAttendanceStatuses,
SearchPersons and SearchActivePersonsForAccount must reject its old nonce with SessionExpired.
Log in again without reloading WebView2; require a different nonce, known active status,
original stable PersonId for Arabic-digit Supervisor SearchPersons, and Unauthorized for
Supervisor SearchActivePersonsForAccount. Then log out and authenticate OrdinaryAdministration
before checking identity and empty items/count on both search actions. C# requires the browser recovery flag before accepting success.
Local validation: python build/test-webview-selfcheck-contract.py (11 checks, including
embedded JS syntax) and python build/test-section08-bridge-contract.py (6 checks) pass.
These preflights do not execute native recovery; Windows CI remains required.
Full screen-consumer failure isolation, all-action gate and schema/Excel matrix remain open.

Failed baseline 4f6bb47e10f9212af51cc75f823a59aaa2861131: Core 42283506732 and Release
42283506612 passed 1060 main tests plus 18 integration and 14 unit tests, then real WebView2
correctly rejected Supervisor account search with Unauthorized. The new test had the wrong
role expectation. Correct only the test to preserve the manifest policy; packaging was blocked.
Corrected native Windows execution remains pending; local syntax/contract preflights pass.

## Packaged x86 timeout investigation (#244)

Head cd615e71e5f871a1dc43aad56a068afeef04d687: Windows Core 42286724207 succeeded.
Release 42286724221 passed pre-package tests and native browser recovery, but the packaged
win-x86 Portable native bridge process hit the exact 180-second watchdog in both attempts
(job 128137835771); the trace ended after UI modal checks, without an internal exception or
bridge-success marker. This proves a timeout, not its precise slow internal operation.
Increase only the expanded packaged native suite's bounded budget to 300 seconds, retain
two attempts, process-tree termination, nonzero-exit rejection and every required evidence
marker. Independent 69-route process remains bounded at 120 seconds. Add public stage-only
native traces during the bridge probe to localize a repeat timeout; no credentials or payloads.
This is test-harness calibration, not a production performance fix or successful x86 claim.
Local inline-JS/contract preflight passes. Corrected x86/x64 native execution is pending CI;
merge remains blocked until Core, Release, four actual packages and provenance all pass.

## Verified recovery baseline (#244) and actual attendance consumer slice

PR #244 final head 403e8fa23feca0a01a837f0c415e8198789ed38b passed Core 42290746058
and Release 42290746016, including packaged x86/x64 native recovery, four actual packages
and independent SHA256 audit. Merged as 55a075d57b16352e236f2c2ab3a5cc23ee0c898a.
Earlier failure notes above remain historical evidence; the bounded 300-second package suite
completed successfully. Manual clean-machine acceptance remains NOT EXECUTED.

Next slice acceptance (test only): invoke shipped AltamamAttendance.render in a connected
WebView2 section under the existing real founder session. Require actual native site/status
options matching IDs and names, real roster row for the created synthetic person with its
selection checkbox, STS-LEAVE exposing mandatory duration fields, and STS-PRESENT hiding
and unrequiring them again. No attendance mutation or injected bridge reply is used.
C# must reject success missing r01AttendanceConsumerVerified. Existing bridge/role/transfer
and recovery gates remain mandatory. No production behavior or optimization changes.
Validation: python build/test-webview-selfcheck-contract.py (11, embedded JS syntax) and
python build/test-section08-bridge-contract.py (6) pass locally; real Windows consumer
execution is pending. This covers initial attendance rendering and duration controls only;
review/commit/dashboard/correction/report flows, failure isolation, all-action runtime gate
and full schemas/Excel parity remain incomplete. Do not declare R01 complete.

## Verified attendance consumer baseline (#245); preview cancellation slice

Head 181869ea0fd1e7f9a382fdb302d6c3840dca27e5 passed Core 42347682029 and
Release 42347681976, including actual consumer rendering, four x86/x64 packages and
independent SHA256 audit. Merged as c07c7ef29848f8dcf90d3e2f04616d8c0b1f0b85.
Manual clean-machine acceptance remains NOT EXECUTED.

Next acceptance (test-only): select the actual saved synthetic person in the shipped attendance
roster, click its real preview button, require the native-backed review modal to show the
correct name/number and save-confirmation action, then click Cancel. Bound modal waits and
require preview controls to recover without a save-success state. Require a browser flag and
independently query SQLCipher AttendanceRecords for the created PersonId: count must remain
zero. Preserve the existing transfer/session/role/status gates; no production change or
optimization claim. Local 11 WebView2 preflights (including embedded JS syntax) and 6
Section08 checks pass; actual Windows execution is pending on the new final head.
This slice does not claim confirmed attendance writes/revision, failure isolation, complete
all-action runtime coverage, or full schema/Excel parity. R01 remains open.

## Verified preview cancellation (#246); confirmed attendance slice

PR #246 head b048dac823c4ceb9a99cd0b68de0a0ee4c97a5e1 passed Core 42349664009 and
Release 42349664026, including four packages and independent SHA256 audit. Merged as
8b33430b4a89a58b926e15322412a8ea572678b8. Manual clean-machine acceptance NOT EXECUTED.

Next test-only acceptance: create a separate synthetic person (00007195), render the actual
attendance screen, select that person's checkbox and Present status, click the actual preview
and confirm buttons, and require the real saved-count UI receipt. Independently require one
SQLCipher AttendanceRecords row for that stable identity and STS-PRESENT, and exactly one
ATTENDANCE_COMMIT audit event. The original cancelled person must still have zero attendance;
the transfer fixture remains independent. C# requires the new commit probe flag and PersonId.
No production fix, auth change, attendance policy change, or performance claim is made.
Local 11 WebView2 preflights (embedded JS syntax) and 6 Section08 checks pass; Windows native
execution is pending on the new final head. All existing Core/Release/package/provenance gates
remain mandatory. R01 is still incomplete: revision/correction/dashboard/report consumer flows,
failure isolation, full executed action matrix and schema/Excel parity remain to be accepted.
