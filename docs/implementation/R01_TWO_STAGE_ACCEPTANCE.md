# R01 remaining acceptance — two owner-approved stages

R01 remains OPEN. Stage completion requires final-head Windows Core and Release,
tests before all four x86/x64 Portable/Installer packages, independent hashes and
review. Manual clean-machine acceptance remains separately deferred / NOT EXECUTED.
No performance optimization, role expansion, historical deletion or enabled restore
is part of either stage. Stage 2 follows Stage 1 acceptance.

## Stage 1 — UI failures, session expiry, person selection and permissions

Stage 1 retains every commit of #256 head
`72fd6852f2da682aefd8937c4bc2395210dba439` (tree
`5fc97933c764411f65f5096015830614bdbf0cc3`). The review is consolidated in #257
against main `72849ff60381ef6389de7a7cf0086c30a4868a68`; #256 is superseded
unmerged. Its stale exceptional-attendance confirmation fix and tests are
retained. The complete Stage 1 tree must pass both workflows before promotion.

| Boundary | Acceptance evidence added | State |
|---|---|---|
| Account picker | Unauthorized/expired/offline/empty search clears previous PersonId and number; recovery; late search after close/navigation ignored; GA/OA confirmation after navigation cannot write | Node real-controller PASS; native Windows picker added, NOT EXECUTED locally |
| Session client | Native expiry clears stored nonce, emits SessionEnded and cancels concurrent old-session reads; role denial keeps session; switching users cancels old requests; late old expiry cannot log out new user; fresh session recovers | Real shipped bridge response-correlation test PASS; native expired attendance UI added, NOT EXECUTED locally |
| Attendance initialization | Offline/denied/expired lookup shows failure, no old selectable roster or write, successful reload recovers | Node real-controller PASS; existing native status/session cases retained |
| Dashboard automatic register | Failed/empty choices retain saved read-only records, forbid stale preview/write, detached late lookup ignored, recovery | #256 real-controller tests retained |
| Correction/settings | Failed status lookup shown; correction person search and settings backup panel remain independently available | Node real-controller PASS |
| Report filters/search | Each site/status lookup succeeds independently when the other fails (offline/denied/expired); person search remains usable; late response cannot repaint a different screen | Node real-controller PASS; existing real native report UI retained |
| Role/identity boundaries | Existing Supervisor deep-link/native denial and native GA/OA cases retained; actual native account picker added for GA/OA, hidden for Supervisor; immutable PersonId and empty-result clearing checked | Full Windows execution pending |

Verified defects before production edits:

- `45cb65c`: failed account search retained TST-ACTIVE-1 rather than clearing it.
- `773929d`: real bridge SessionExpired frame retained the current stored nonce.
- `50c95ab`: failed statuses discarded the independently successful site filter.

Each defect was reproduced by executing the shipped JS at its actual consumer or
message-correlation boundary, then repaired in that owning layer. These Node tests
supply DOM/native reply boundaries; they do not replace native SQLCipher acceptance.
The Windows harness uses real authentication, dispatcher and SQLCipher with no
injected success/error replies, checks native GA/OA picker identity and empty search,
Supervisor restriction and an expired attendance screen, and requires two new
completion flags before the existing independent database assertions can pass.
It operates only on the established disposable synthetic fixture.

Local final checks: all 21 JavaScript suites PASS; WebView preflight 11 PASS,
Section08 contract 6 PASS, native-evidence validator 12 PASS, embedded WebView JS
syntax and diff whitespace PASS. Windows/WPF execution of the added scenarios is
NOT EXECUTED locally; final-head CI is mandatory before declaring Stage 1 accepted.

## Stage 2 — error variants, report/Excel parity, recovery UI and final acceptance

Start after Stage 1 acceptance. Preserve the 19-case / 103-enabled-action native
execution gate and intentionally disabled RestoreDatabaseAtomically.

1. Reconcile remaining error/null/receipt/invalidation and branch/role evidence;
   verify truthful errors and no writes, audit duplication or sensitive data leaks.
2. Complete residual report/Excel mode/filter/pagination/historical parity cells
   with real workbook bytes and independently known native SQL outcomes.
3. Execute backup inspection/read-only restore preview and full-reset UI consumers,
   including expired sessions, refusal/recovery and truthful committed-reset success.
   Any destructive reset is confined to a disposable fixture with encrypted rescue.
4. Reconcile every R01 requirement against executed evidence and final Windows
   package/hash checks. Report any unexecuted cell explicitly; action inventory or
   green routing checks alone do not close R01 or authorize production publishing.

Stage 2 implementation is now a review candidate; its own exact-head Windows
execution and package/hash acceptance remain required. Stage 1 alone did not
prove these added scenarios.


## Windows failure 42410644404 / 42410644315 — shared test clock repair

Both exact-head runs passed compilation and 1084 main tests, 18 integration tests,
14 authorization tests and the 19-case / 103-action native gate. Both failed in
real WPF/WebView2 at r01-attendance-preview-cancel; package/audit jobs correctly
remained skipped. Core failed at 20:34 UTC, Release at 20:43 UTC. The disposable
host used TimeProvider.System, while attendance refuses new previews at the
unchanged default 20:00 cutoff. This made the success-path browser fixture depend
on when a runner was assigned, rather than indicating a Git merge conflict.

A real authenticated native SQLCipher regression reproduces the 21:00 refusal:
HistoricDayLocked, the actual cutoff message, no AttendanceRecords or audit write.
A paired test runs real preview/commit with the new disposable-host clock and
independently verifies one saved attendance record and one new audit event.
Pre-fix WebView wiring guard: 1 failed / 11 passed, test-first commit 16a69ab.
The failed Windows runs are the actual browser baseline; no local Windows success
is claimed from Python source checks or Node consumers.

Only the isolated --webview-bridge-e2e branch receives a shared noon business clock.
The native services, historical fixture dates and browser Date use the same local
business date. Native and browser clocks continue advancing with monotonic elapsed
time; explicit date construction/UTC parsing and Date subclassing are retained.
No production schedule, role, lock condition, system clock or user data is changed.
The diagnostic now includes the actual synthetic UI warning on preview failure.

Local repair verification: 7 C# tests passed (five clock hour/timezone rows and two
real native SQLCipher cutoff/noon outcomes); 13 WebView preflights passed including
execution of the actual injected browser clock, 21 JS suites and six Section08
checks passed. Native evidence validator regressions remain passing. The real
Windows E2E and packages on the repaired head remain REQUIRED / NOT EXECUTED locally.
Consolidation avoids merging a failed prerequisite: #257 carries all #256 changes
and the repair, and must pass against main before any merge or Stage 2 work.

## Follow-up Windows failure — account initialization ordering

Repaired-clock head `fbc151be3f468350fa517695ad37934901a86e7a` passed
1091 main tests plus 18 integration/14 authorization tests in both runs
42413405342 and 42413405362. The real browser progressed beyond attendance
preview/save/revision and transfer checks, then timed out waiting for native
account-picker identity. Its old stage label still said QueryMovementArchive;
the actual reason in BOTH job logs was "native consumer did not reach account
search identity". Packages and independent audit correctly remained skipped.

The add button existed and was enabled while GetAllowedRoles was pending. The
browser helper clicked it immediately. The form captured currentRequest before
accounts() started its initial load(), which increments that request generation.
The form's existing ownership guard then correctly rejected every search. Fast
supplied replies in the previous Node tests did not exercise this startup race.

Test-first commit 405a818 reproduces delayed role/list completion through the
shipped controller. Before correction it fails because the initial add action is
enabled before native roles arrive. The full regression additionally opens and
searches while the initial list remains pending, completes that list, and searches
again, under both GA/OA roles. Other tests retain a disabled add action on denied,
expired or offline role reads and ignore a late role reply after navigation to a
Supervisor session. No service, role, schema or write authority is modified.

The owning UI now disables add until roles are known and starts the first list
request BEFORE enabling the editor. Its generation is therefore current when
opened; queued activations before readiness are rejected. The real Windows
harness waits for the enabled add action and records the correct owner/admin
picker stage. Native authentication/SQLCipher and both completion flags remain
mandatory. No delay or timeout increase, fake success, or removed assertion is used.

Local follow-up: all 21 JS suites, 13 WebView preflights (including the shipped
inline JS parser), six Section08 checks, twelve native evidence validator tests
and whitespace checks pass. The new final-head Windows browser/package/hash runs
are still REQUIRED; no local Windows success or Stage 1 acceptance is claimed.


## Stage 1 accepted — #257

Head 03bee88bad5c69f3324b585d13407a00f5928db9 passed Core 42416970467 and
Release 42416970433, each with 1091 main tests plus 18 integration/14 unit tests,
The gate at that baseline was still 19 native cases / 103 enabled actions.
Actual native WPF/WebView2, both x86/x64 Portable/Installer acceptance and independent
four-file SHA256 audit job 128547085754 passed; all four artifacts were nonexpired
and identified that same head. No blocking reviews/comments and a clean main merge.
Merge: af5dec792876f5dfd55296691b5ce08156bf07b9. Manual clean-machine acceptance
remains owner-deferred / NOT EXECUTED.

## Stage 2 implementation candidate

- Actual native filtered single-person report and XLSX parity for nonempty Present,
  nonempty Absent and empty site/status results; historical identity and known
  104 source attendance records stay intact. Extend the committed corpus by one
  observed empty-report shape; keep strict comparison and exactly 103 action keys.
- All remaining personnel export modes: real internal/external transfers and
  confirmed delayed separation, native archive reads, genuine workbook cell values,
  correct local ordinals and UTC instant parity. Explicit date/time normalization
  compares the same instant, not timezone string spelling.
- Native malformed report/export requests: unknown mode/type, duplicate identities,
  invalid export identifier, reversed dates, revoked session; truthful error/null,
  no published/partial workbooks, no failed-command attendance or audit writes.
  Intentional Logout audit is retained and measured separately.
- Supplied-boundary UI regressions: rejected/corrupt inspection clears a formerly
  valid selection; inconsistent digest or enabled-restore receipt cannot show a
  successful comparison; destructive restore is never invoked.
- Verified reset-consumer defect: a review approved after leaving settings could
  construct the final form or dispatch reset. Test-first b52bfff fails on this
  behavior before production edits. Bind preview/final confirmation to the owning
  connected settings screen and current generation; cancellation invalidates it.
- Actual Windows recovery UI harness: real founder login form, real native backup,
  corrupt then valid inspection, read-only comparison, abandoned final reset approval,
  wrong password refusal, then successful confirmed reset and first setup. Only OS
  file selection is a supplied path delegate inside the disposable fixture. No
  native response is injected. Independent C# verifies empty intact live DB, original
  people/users in the encrypted rescue and retention of an unregistered test file.
  The original independent SQL assertions and all 69 route checks run BEFORE reset.

Both native TRX gates now require all 22 named cases / the same 103 enabled action
keys, including the three added report/error/archive acceptance cases. The added
Windows recovery harness must finish before the process reports success. All tests,
production lock/roles/encryption and package/process budgets stay mandatory.

Local capture fixture assumptions corrected before final verification: numeric Excel
UTC spelling is semantically normalized; successful Logout owns an audit record.
These are not claimed production defects. The empty native response was captured
only from passed real tests, then strict committed verification was restored; no
capture/update bypass is committed. See R01_FINAL_ACCEPTANCE_REVIEW.md for final
local checks and the remaining exact-head Windows acceptance gate.
