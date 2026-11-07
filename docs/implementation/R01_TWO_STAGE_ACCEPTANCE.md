# R01 remaining acceptance — two owner-approved stages

R01 remains OPEN. Stage completion requires final-head Windows Core and Release,
tests before all four x86/x64 Portable/Installer packages, independent hashes and
review. Manual clean-machine acceptance remains separately deferred / NOT EXECUTED.
No performance optimization, role expansion, historical deletion or enabled restore
is part of either stage. Stage 2 follows Stage 1 acceptance.

## Stage 1 — UI failures, session expiry, person selection and permissions

Baseline: #256 head `72fd6852f2da682aefd8937c4bc2395210dba439`
(tree `5fc97933c764411f65f5096015830614bdbf0cc3`). #256 is a separate pending
prerequisite; its stale exceptional-attendance confirmation fix is retained.
This follow-up must not be merged ahead of that prerequisite.

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

Stage 2 is not claimed implemented or accepted by Stage 1 changes.
