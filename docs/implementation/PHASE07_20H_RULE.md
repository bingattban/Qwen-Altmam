# PHASE 07 — Canonical Meaning of the 20:00 Automatic Attendance Rule (07.01)

This document fixes the semantics of the «تحضير الساعة الثامنة مساءً» rule BEFORE implementation,
as required by step 07.01. It is derived from the canonical schema (V001 `AutoAttendanceRuns`
ledger), the existing domain policies (`AttendanceDayPolicy`, `AssignmentPeriodPolicy`) and the
EightPmScheduler screen contract. Every statement below is enforced by C#/SQL code, not UI state.

## 1. Date basis
- The scheduler fires at **local business time 20:00** on date `D`.
- The attendance rows it writes carry `AttendanceLocalDate = D` (the *current* business date).
- The status copied for each person comes from that person's committed row for `D - 1`
  ("carried-forward status selection").
- Business-date authority is the host `TimeProvider` local clock; a persisted
  `LastObservedUtcTimestamp` in `AppSettings` detects wall-clock rollback (§6).

## 2. Carried-forward status selection
For every active person (`Persons.ActiveFlag = 1`) with an open assignment covering `D`
(`ValidFromLocalDate <= D` and `ValidToLocalDate IS NULL OR > D`):
- If a committed `AttendanceRecords` row exists for `D-1`, its **status snapshot triple**
  (`StatusId`, `StatusNameSnapshot`, `StatusSymbolSnapshot`, `StatusColorSnapshot`) and its
  effective site are carried into the new row for `D`.
- Site membership is re-resolved for `D` (assignments are effective-dated); when the current
  assignment differs from yesterday's record, the new row uses the *current* assignment/site
  snapshots — never stale ones.

## 3. Exclusions
- **No-yesterday-record persons**: skipped (`SkippedNoYesterdayCount`). They remain visible in
  UnpreparedPersonnel and can be prepared manually or via bulk preview/confirm.
- **Transfer-effective dates**: a person with any `PersonnelMovements` row whose
  `EffectiveLocalDate = D` is excluded entirely (`SkippedTransferExemptCount`) — consistent with
  `AttendanceDayPolicy.EnsureDayCanBePrepared` which forbids preparing movement days.
- **Archived/inactive persons** (`ActiveFlag = 0`) are never auto-prepared.
- **Closed dates**: if `PeriodClosures` contains key `D` (DAY) or `YYYY-MM` (MONTH), the run is
  blocked (`HistoricDayLocked`); no rows are written and no ledger completion is recorded.

## 4. Manual precedence
The run is INSERT-only against `UNIQUE(PersonId, AttendanceLocalDate)`:
- Any legitimate existing manual/bulk row for `D` (including same-day revisions) is **never
  overwritten**; the person is counted as skipped-manual.
- Rows created by the 20:00 mechanism use `Source IN ('20h-auto','20h-catchup')`; later manual
  edits of those rows go through the normal revision path (old evidence preserved in
  `AttendanceRevisions`).

## 5. Idempotency & ledger
- One ledger row per business date: `JobKey = AUTO20H-YYYY-MM-DD`, `BusinessDate UNIQUE`.
- A second attempt for the same date is a no-op success reporting the stored ledger summary
  (`IsCatchUpRun` distinguishes scheduled vs. catch-up execution).
- The ledger records `CopiedCount`, `SkippedNoYesterdayCount`, `SkippedTransferExemptCount`,
  due time, start/completion UTC timestamps and an Arabic summary. Actor attribution for all
  written rows is `USR-SYSTEM` (scheduler service account).

## 6. Missed schedule / catch-up rules (approved)
On startup (and on every scheduler tick) the host checks the last ledger entry:
- Pending dates are business dates strictly after the last completed `BusinessDate`, up to and
  including today only if local time ≥ 20:00.
- **Bounded catch-up window** (default 7 days, configurable via `AutoAttendanceCatchUpDays` in
  `AppSettings`): older missed dates are reported as unrecoverable in the summary rather than
  silently back-filled, preventing mass phantom attendance after long shutdowns.
- Catch-up runs write `Source='20h-catchup'`, `IsCatchUpRun=1`, and process dates in ascending
  order so each carried-forward day reads the row committed by the previous catch-up day.
- **Clock rollback detection**: when the observed UTC timestamp moves backwards beyond a
  tolerance, the scheduler refuses the run and reports `ClockRollbackSuspected`; nothing is
  written twice because ledger/idempotency keys remain authoritative.

## 7. Partial-failure safety
The whole day's copy executes inside ONE SQLite write transaction (host write gate + explicit
transaction). Any exception rolls back every inserted row; the ledger gets no completion row, so
a retry re-attempts the same date cleanly. There is therefore no possible half-committed 20:00
run and no duplicate rows even across crashes between attempts (replay is bounded by
`UNIQUE(BusinessDate)` in `AutoAttendanceRuns` plus deterministic idempotency keys
`AUTO20H-{date}-{personId}`).

## 8. Offline mode
Everything is local SQLCipher; "offline" here means the machine was powered down or disconnected
from any network — the catch-up rules in §6 apply unchanged. No JavaScript timer participates:
the durable scheduler lives in the C# host (Task 07.02).
